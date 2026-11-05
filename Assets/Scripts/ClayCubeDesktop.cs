using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Desktop test version of the clay cube. No headset / Meta SDK needed.
///
/// Setup: new scene -> create an Empty GameObject -> add this script. Press Play.
/// (It adds MeshFilter/MeshRenderer, a material, a cursor sphere and frames the camera itself.)
///
/// Controls:
///   Mouse move     : moves the cursor sphere (at a fixed depth in front of the camera)
///   Mouse wheel    : push the cursor sphere deeper / closer to the camera
///   Left button    : sculpt (sphere pushes the clay away = dents)
///   Right button   : grab and drag the clay near the sphere (pull / stretch)
///   Q / E          : rotate the cube around Y
///   R              : reset the cube
///
/// Works with both the old Input Manager and the new Input System package.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ClayCubeDesktop : MonoBehaviour
{
    [Header("Mesh")]
    public int resolution = 48;
    public float size = 0.2f;

    [Header("Tool")]
    public float toolRadius = 0.012f;
    public float grabRadius = 0.04f;
    public float scrollSpeed = 0.02f;

    [Header("Scene")]
    public bool autoFrameCamera = true;
    public Color clayColor = new Color(0.78f, 0.45f, 0.35f);

    Mesh mesh;
    NativeArray<float3> verts, normals, startVerts;
    NativeArray<int> tris;
    NativeArray<float> weights;
    NativeArray<float4> tips;
    JobHandle handle;
    bool pending;

    Camera cam;
    Transform cursor;
    float depth;
    bool grabbing;
    float3 lastGrabLocal;

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        cam = FindCamera();
        BuildWeldedCube(out var vList, out var tList);

        verts = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        startVerts = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        normals = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        weights = new NativeArray<float>(vList.Count, Allocator.Persistent);
        tris = new NativeArray<int>(tList.ToArray(), Allocator.Persistent);
        tips = new NativeArray<float4>(1, Allocator.Persistent);

        for (int i = 0; i < vList.Count; i++) { verts[i] = vList[i]; startVerts[i] = vList[i]; }

        mesh = new Mesh { name = "ClayCubeDesktop" };
        if (vList.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.SetVertices(vList);
        mesh.SetTriangles(tList, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        GetComponent<MeshFilter>().sharedMesh = mesh;

        var mat = MakeMaterial(clayColor);
        GetComponent<MeshRenderer>().sharedMaterial = mat;

        // Cursor sphere
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(s.GetComponent<Collider>());
        s.name = "ClayCursor";
        s.transform.localScale = Vector3.one * toolRadius * 2f;
        s.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(new Color(0.2f, 0.8f, 1f));
        cursor = s.transform;

        if (cam != null && autoFrameCamera)
        {
            cam.nearClipPlane = 0.01f;
            cam.transform.position = transform.position + new Vector3(0f, size * 0.3f, -size * 2.5f);
            cam.transform.LookAt(transform.position);
        }

        depth = cam != null
            ? Vector3.Distance(cam.transform.position, transform.position) - size * 0.6f
            : 0.3f;
    }

    static Camera FindCamera()
    {
        // Camera.main is null if the camera isn't tagged "MainCamera".
        var c = Camera.main;
        if (c == null) c = FindFirstObjectByType<Camera>();
        if (c == null) Debug.LogWarning("ClayCubeDesktop: no Camera found in the scene.");
        return c;
    }

    void OnGUI()
    {
        // Debug readout so you can see what the script thinks is happening.
        var input = ReadInput();
        string camName = cam != null ? cam.name : "NONE";
        GUI.Label(new Rect(10, 10, 700, 25),
            $"cam: {camName} | mouse: {input.mouse} | depth: {depth:F3} | L:{input.left} R:{input.right}");
    }

    static Material MakeMaterial(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    void BuildWeldedCube(out List<Vector3> vList, out List<int> tList)
    {
        int n = resolution, s = n + 1;
        var map = new int[s * s * s];
        System.Array.Fill(map, -1);
        var vl = new List<Vector3>();
        var tl = new List<int>();

        int Get(int x, int y, int z)
        {
            int k = x + s * (y + s * z);
            if (map[k] < 0)
            {
                map[k] = vl.Count;
                vl.Add((new Vector3(x, y, z) / n - Vector3.one * 0.5f) * size);
            }
            return map[k];
        }

        int V(int axis, int side, int a, int b)
        {
            var c = new int[3];
            c[axis] = side * n;
            c[(axis + 1) % 3] = a;
            c[(axis + 2) % 3] = b;
            return Get(c[0], c[1], c[2]);
        }

        for (int axis = 0; axis < 3; axis++)
        for (int side = 0; side < 2; side++)
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int p00 = V(axis, side, i, j),     p10 = V(axis, side, i + 1, j);
            int p01 = V(axis, side, i, j + 1), p11 = V(axis, side, i + 1, j + 1);
            if (side == 1) { tl.Add(p00); tl.Add(p10); tl.Add(p01); tl.Add(p10); tl.Add(p11); tl.Add(p01); }
            else           { tl.Add(p00); tl.Add(p01); tl.Add(p10); tl.Add(p10); tl.Add(p01); tl.Add(p11); }
        }

        vList = vl;
        tList = tl;
    }

    // ------------------------------------------------------------------ input

    struct InputState
    {
        public Vector2 mouse;
        public float scroll;
        public bool left, right, q, e, r;
    }

    static InputState ReadInput()
    {
        var s = new InputState();
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        var k = Keyboard.current;
        if (m != null)
        {
            s.mouse = m.position.ReadValue();
            s.scroll = Mathf.Sign(m.scroll.ReadValue().y) * (Mathf.Abs(m.scroll.ReadValue().y) > 0.01f ? 1f : 0f);
            s.left = m.leftButton.isPressed;
            s.right = m.rightButton.isPressed;
        }
        if (k != null)
        {
            s.q = k.qKey.isPressed;
            s.e = k.eKey.isPressed;
            s.r = k.rKey.wasPressedThisFrame;
        }
#else
        s.mouse = Input.mousePosition;
        s.scroll = Mathf.Sign(Input.mouseScrollDelta.y) * (Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f ? 1f : 0f);
        s.left = Input.GetMouseButton(0);
        s.right = Input.GetMouseButton(1);
        s.q = Input.GetKey(KeyCode.Q);
        s.e = Input.GetKey(KeyCode.E);
        s.r = Input.GetKeyDown(KeyCode.R);
#endif
        return s;
    }

    // ----------------------------------------------------------------- update

    void Update()
    {
        if (cam == null) cam = FindCamera();
        if (cam == null) return;
        var input = ReadInput();

        // Rotate / reset cube
        if (input.q) transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
        if (input.e) transform.Rotate(0f, -90f * Time.deltaTime, 0f, Space.World);
        if (input.r)
        {
            handle.Complete();
            pending = false;
            for (int i = 0; i < verts.Length; i++) verts[i] = startVerts[i];
            mesh.SetVertices(verts.Reinterpret<Vector3>());
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        // Cursor position
        depth = Mathf.Max(0.05f, depth + input.scroll * scrollSpeed);
        Ray ray = cam.ScreenPointToRay(input.mouse);
        cursor.position = ray.origin + ray.direction * depth;
        float3 cursorLocal = transform.InverseTransformPoint(cursor.position);
        float scale = transform.lossyScale.x;

        JobHandle dep = default;
        int tipCount = 0;

        // Right button: grab / drag
        if (input.right)
        {
            if (!grabbing)
            {
                var wJob = new GrabWeightsJob
                {
                    verts = verts,
                    weights = weights,
                    center = cursorLocal,
                    radius = grabRadius / scale
                };
                dep = wJob.Schedule(verts.Length, 256, dep);
                grabbing = true;
                lastGrabLocal = cursorLocal;
            }
            else
            {
                float3 delta = cursorLocal - lastGrabLocal;
                if (math.lengthsq(delta) > 0f)
                {
                    var mJob = new GrabMoveJob { verts = verts, weights = weights, delta = delta };
                    dep = mJob.Schedule(verts.Length, 256, dep);
                }
                lastGrabLocal = cursorLocal;
            }
        }
        else
        {
            grabbing = false;
        }

        // Left button: sculpt (sphere pushes clay out of the way)
        if (input.left && !input.right)
        {
            tips[0] = new float4(cursorLocal.x, cursorLocal.y, cursorLocal.z, toolRadius / scale);
            tipCount = 1;
        }

        if (tipCount == 0 && !grabbing)
        {
            dep.Complete();
            return;
        }

        var poke = new PokeJob { verts = verts, tips = tips, tipCount = tipCount };
        var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
        dep = poke.Schedule(verts.Length, 256, dep);
        handle = norm.Schedule(dep);
        pending = true;
    }

    void LateUpdate()
    {
        if (!pending) return;
        handle.Complete();
        pending = false;

        mesh.SetVertices(verts.Reinterpret<Vector3>());
        mesh.SetNormals(normals.Reinterpret<Vector3>());
        mesh.RecalculateBounds();
    }

    void OnDestroy()
    {
        handle.Complete();
        if (verts.IsCreated) verts.Dispose();
        if (startVerts.IsCreated) startVerts.Dispose();
        if (normals.IsCreated) normals.Dispose();
        if (weights.IsCreated) weights.Dispose();
        if (tris.IsCreated) tris.Dispose();
        if (tips.IsCreated) tips.Dispose();
    }

    // ------------------------------------------------------------------- jobs

    [BurstCompile]
    struct PokeJob : IJobParallelFor
    {
        public NativeArray<float3> verts;
        [ReadOnly] public NativeArray<float4> tips;
        public int tipCount;

        public void Execute(int i)
        {
            float3 p = verts[i];
            for (int t = 0; t < tipCount; t++)
            {
                float3 c = tips[t].xyz;
                float r = tips[t].w;
                float3 d = p - c;
                float sq = math.lengthsq(d);
                if (sq < r * r)
                {
                    float len = math.sqrt(sq);
                    d = len > 1e-6f ? d / len : new float3(0, 1, 0);
                    p = c + d * r;
                }
            }
            verts[i] = p;
        }
    }

    [BurstCompile]
    struct GrabWeightsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> verts;
        public NativeArray<float> weights;
        public float3 center;
        public float radius;

        public void Execute(int i)
        {
            float d = math.distance(verts[i], center);
            float w = 1f - math.saturate(d / radius);
            weights[i] = w * w * (3f - 2f * w);
        }
    }

    [BurstCompile]
    struct GrabMoveJob : IJobParallelFor
    {
        public NativeArray<float3> verts;
        [ReadOnly] public NativeArray<float> weights;
        public float3 delta;

        public void Execute(int i)
        {
            float w = weights[i];
            if (w > 0f) verts[i] += delta * w;
        }
    }

    [BurstCompile]
    struct NormalsJob : IJob
    {
        [ReadOnly] public NativeArray<float3> verts;
        [ReadOnly] public NativeArray<int> tris;
        public NativeArray<float3> normals;

        public void Execute()
        {
            for (int i = 0; i < normals.Length; i++) normals[i] = float3.zero;

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                float3 n = math.cross(verts[b] - verts[a], verts[c] - verts[a]);
                normals[a] += n;
                normals[b] += n;
                normals[c] += n;
            }

            for (int i = 0; i < normals.Length; i++)
                normals[i] = math.normalizesafe(normals[i], new float3(0, 1, 0));
        }
    }
}
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Desktop test version of ClayCube. No headset / Meta SDK needed.
/// Uses the same reshape method as ClayCube: every vertex slides along the line toward the
/// cube's centre and can only be pushed IN (depth never decreases), so the surface can't fold.
///
/// Setup: new scene -> create an Empty GameObject -> add this script. Press Play.
/// (It adds MeshFilter/MeshRenderer, a material, a cursor sphere and frames the camera itself.)
///
/// Controls:
///   Mouse move     : moves the cursor sphere (at a fixed depth in front of the camera)
///   Mouse wheel    : push the cursor sphere deeper / closer to the camera
///   Left button    : carve (hold and drag; fast mouse movement is swept, so no gaps)
///   Z              : undo the last stroke
///   R              : reset the cube
///   Q / E          : rotate the cube around Y
///
/// Works with both the old Input Manager and the new Input System package.
/// Requires: Burst, Collections, Mathematics.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ClayCubeDesktop : MonoBehaviour
{
    [Header("Mesh")]
    public int resolution = 48;
    public float size = 0.2f;

    [Header("Tool")]
    [Tooltip("Radius of the cursor sphere in meters.")]
    public float toolRadius = 0.012f;
    public float scrollSpeed = 0.02f;

    [Header("Carving")]
    [Tooltip("Clay can't be pushed closer than this (meters) to the cube's centre.")]
    public float coreRadius = 0.03f;
    [Tooltip("Cleanup passes applied after each stroke. 0 = off.")]
    public int smoothIterations = 1;
    [Range(0f, 1f)] public float smoothStrength = 0.35f;
    public int maxUndo = 15;

    [Header("Scene")]
    public bool autoFrameCamera = true;
    public Color clayColor = new Color(0.78f, 0.45f, 0.35f);

    struct Capsule
    {
        public float3 a;
        public float3 b;
        public float r;
    }

    // ---- mesh + clay data
    Mesh mesh;
    NativeArray<float3> rest, dir, verts, normals;
    NativeArray<float> depth, depthTmp;
    NativeArray<int> tris, neighborStart, neighborIdx;
    NativeArray<Capsule> capsules;
    NativeArray<int> changed;
    JobHandle handle;
    bool pending;

    // ---- stroke / undo
    bool inStroke, strokeChanged;
    float[] strokeSnapshot;
    readonly List<float[]> undoStack = new List<float[]>();
    float3 lastLocal;

    // ---- scene
    Camera cam;
    Transform cursor;
    float cursorDepth;

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        cam = FindCamera();
        BuildWeldedCube(out var restList, out var tList);
        int count = restList.Count;

        rest = new NativeArray<float3>(count, Allocator.Persistent);
        dir = new NativeArray<float3>(count, Allocator.Persistent);
        verts = new NativeArray<float3>(count, Allocator.Persistent);
        normals = new NativeArray<float3>(count, Allocator.Persistent);
        depth = new NativeArray<float>(count, Allocator.Persistent);
        depthTmp = new NativeArray<float>(count, Allocator.Persistent);
        tris = new NativeArray<int>(tList.ToArray(), Allocator.Persistent);
        capsules = new NativeArray<Capsule>(4, Allocator.Persistent);
        changed = new NativeArray<int>(1, Allocator.Persistent);

        for (int i = 0; i < count; i++)
        {
            rest[i] = restList[i];
            verts[i] = restList[i];
            dir[i] = -math.normalize((float3)restList[i]); // radially toward the centre
        }

        BuildNeighbors(count, tList);

        mesh = new Mesh { name = "ClayCubeDesktop" };
        if (count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.SetVertices(restList);
        mesh.SetTriangles(tList, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds(); // vertices only move inward, so these bounds stay valid
        GetComponent<MeshFilter>().sharedMesh = mesh;
        GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(clayColor);

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

        cursorDepth = cam != null
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

    void BuildWeldedCube(out List<Vector3> restList, out List<int> tList)
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

        restList = vl;
        tList = tl;
    }

    void BuildNeighbors(int count, List<int> t)
    {
        var sets = new HashSet<int>[count];
        for (int i = 0; i < count; i++) sets[i] = new HashSet<int>();
        for (int k = 0; k < t.Count; k += 3)
        {
            int a = t[k], b = t[k + 1], c = t[k + 2];
            sets[a].Add(b); sets[a].Add(c);
            sets[b].Add(a); sets[b].Add(c);
            sets[c].Add(a); sets[c].Add(b);
        }

        var start = new int[count + 1];
        var idx = new List<int>();
        for (int i = 0; i < count; i++)
        {
            start[i] = idx.Count;
            idx.AddRange(sets[i]);
        }
        start[count] = idx.Count;

        neighborStart = new NativeArray<int>(start, Allocator.Persistent);
        neighborIdx = new NativeArray<int>(idx.ToArray(), Allocator.Persistent);
    }

    // ------------------------------------------------------------------ input

    struct InputState
    {
        public Vector2 mouse;
        public float scroll;
        public bool left, q, e, rDown, zDown;
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
            float sy = m.scroll.ReadValue().y;
            s.scroll = Mathf.Abs(sy) > 0.01f ? Mathf.Sign(sy) : 0f;
            s.left = m.leftButton.isPressed;
        }
        if (k != null)
        {
            s.q = k.qKey.isPressed;
            s.e = k.eKey.isPressed;
            s.rDown = k.rKey.wasPressedThisFrame;
            s.zDown = k.zKey.wasPressedThisFrame;
        }
#else
        s.mouse = Input.mousePosition;
        float sy = Input.mouseScrollDelta.y;
        s.scroll = Mathf.Abs(sy) > 0.01f ? Mathf.Sign(sy) : 0f;
        s.left = Input.GetMouseButton(0);
        s.q = Input.GetKey(KeyCode.Q);
        s.e = Input.GetKey(KeyCode.E);
        s.rDown = Input.GetKeyDown(KeyCode.R);
        s.zDown = Input.GetKeyDown(KeyCode.Z);
#endif
        return s;
    }

    void OnGUI()
    {
        // Debug readout so you can see what the script thinks is happening.
        var input = ReadInput();
        string camName = cam != null ? cam.name : "NONE";
        GUI.Label(new Rect(10, 10, 800, 25),
            $"cam: {camName} | mouse: {input.mouse} | depth: {cursorDepth:F3} | carving: {input.left} | undo steps: {undoStack.Count}");
    }

    // ----------------------------------------------------------------- update

    void Update()
    {
        if (cam == null) cam = FindCamera();
        if (cam == null) return;
        var input = ReadInput();

        // Rotate cube
        if (input.q) transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
        if (input.e) transform.Rotate(0f, -90f * Time.deltaTime, 0f, Space.World);

        if (input.rDown) ResetClay();
        if (input.zDown) Undo();

        // Cursor position
        cursorDepth = Mathf.Max(0.05f, cursorDepth + input.scroll * scrollSpeed);
        Ray ray = cam.ScreenPointToRay(input.mouse);
        cursor.position = ray.origin + ray.direction * cursorDepth;
        float3 cursorLocal = transform.InverseTransformPoint(cursor.position);
        float scale = transform.lossyScale.x;

        if (input.left)
        {
            if (!inStroke)
            {
                BeginStroke();
                lastLocal = cursorLocal;
            }

            // Capsule swept from last frame's cursor position to this frame's, so fast mouse
            // movement can't skip over vertices.
            float r = toolRadius / scale;
            if (OverlapsBounds(lastLocal, cursorLocal, r))
            {
                capsules[0] = new Capsule { a = lastLocal, b = cursorLocal, r = r };

                var carve = new CarveJob
                {
                    rest = rest,
                    dir = dir,
                    depth = depth,
                    verts = verts,
                    capsules = capsules,
                    capsuleCount = 1,
                    core = coreRadius / scale,
                    changed = changed
                };
                var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
                handle = norm.Schedule(carve.Schedule(verts.Length, 256));
                pending = true;
            }
            lastLocal = cursorLocal;
        }
        else if (inStroke)
        {
            EndStroke();
        }
    }

    void LateUpdate()
    {
        FinishPending();
    }

    bool OverlapsBounds(float3 a, float3 b, float r)
    {
        float half = size * 0.5f + r;
        if (math.min(a.x, b.x) > half || math.max(a.x, b.x) < -half) return false;
        if (math.min(a.y, b.y) > half || math.max(a.y, b.y) < -half) return false;
        if (math.min(a.z, b.z) > half || math.max(a.z, b.z) < -half) return false;
        return true;
    }

    /// <summary>Completes the carve jobs (if any) and uploads the result to the mesh.</summary>
    void FinishPending()
    {
        if (!pending) return;
        handle.Complete();
        pending = false;

        if (changed[0] != 0)
        {
            strokeChanged = true;
            changed[0] = 0;
        }
        UploadMesh();
    }

    void UploadMesh()
    {
        mesh.SetVertices(verts.Reinterpret<Vector3>());
        mesh.SetNormals(normals.Reinterpret<Vector3>());
    }

    // ------------------------------------------------------------ strokes + undo

    void BeginStroke()
    {
        strokeSnapshot = depth.ToArray();
        strokeChanged = false;
        inStroke = true;
    }

    void EndStroke()
    {
        FinishPending();
        inStroke = false;

        if (strokeChanged && strokeSnapshot != null)
        {
            undoStack.Add(strokeSnapshot);
            if (undoStack.Count > maxUndo) undoStack.RemoveAt(0);
            SmoothAfterStroke();
        }
        strokeSnapshot = null;
        strokeChanged = false;
    }

    void SmoothAfterStroke()
    {
        if (smoothIterations <= 0 || smoothStrength <= 0f) return;

        for (int it = 0; it < smoothIterations; it++)
        {
            var job = new DilateSmoothJob
            {
                src = depth,
                dst = depthTmp,
                start = neighborStart,
                idx = neighborIdx,
                strength = smoothStrength
            };
            job.Schedule(depth.Length, 256).Complete();
            depth.CopyFrom(depthTmp);
        }
        RebuildMesh();
    }

    /// <summary>Undo the last stroke.</summary>
    public void Undo()
    {
        FinishPending();
        if (inStroke) EndStroke();
        if (undoStack.Count == 0) return;

        var snap = undoStack[undoStack.Count - 1];
        undoStack.RemoveAt(undoStack.Count - 1);
        depth.CopyFrom(snap);
        RebuildMesh();
    }

    /// <summary>Restore the original cube and clear undo history.</summary>
    public void ResetClay()
    {
        FinishPending();
        inStroke = false;
        strokeSnapshot = null;
        strokeChanged = false;
        undoStack.Clear();
        for (int i = 0; i < depth.Length; i++) depth[i] = 0f;
        RebuildMesh();
    }

    void RebuildMesh()
    {
        var apply = new ApplyDepthJob { rest = rest, dir = dir, depth = depth, verts = verts };
        var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
        norm.Schedule(apply.Schedule(verts.Length, 256)).Complete();
        UploadMesh();
    }

    void OnDestroy()
    {
        handle.Complete();
        if (rest.IsCreated) rest.Dispose();
        if (dir.IsCreated) dir.Dispose();
        if (verts.IsCreated) verts.Dispose();
        if (normals.IsCreated) normals.Dispose();
        if (depth.IsCreated) depth.Dispose();
        if (depthTmp.IsCreated) depthTmp.Dispose();
        if (tris.IsCreated) tris.Dispose();
        if (neighborStart.IsCreated) neighborStart.Dispose();
        if (neighborIdx.IsCreated) neighborIdx.Dispose();
        if (capsules.IsCreated) capsules.Dispose();
        if (changed.IsCreated) changed.Dispose();
    }

    // ------------------------------------------------------------------- jobs
    // Identical to the jobs in ClayCube.

    /// <summary>
    /// Push-in-only carving. Each vertex slides along its fixed inward direction until it is
    /// outside every capsule. Depth never decreases.
    /// </summary>
    [BurstCompile]
    struct CarveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> rest;
        [ReadOnly] public NativeArray<float3> dir;
        public NativeArray<float> depth;
        public NativeArray<float3> verts;
        [ReadOnly] public NativeArray<Capsule> capsules;
        public int capsuleCount;
        public float core;
        [NativeDisableParallelForRestriction] public NativeArray<int> changed;

        public void Execute(int i)
        {
            float3 r = rest[i];
            float3 u = dir[i];
            float d0 = depth[i];
            float d = d0;
            float limit = math.max(0f, math.length(r) - core);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int c = 0; c < capsuleCount; c++)
                {
                    Capsule cap = capsules[c];
                    float3 ab = cap.b - cap.a;
                    float abLen2 = math.max(math.dot(ab, ab), 1e-8f);
                    float r2 = cap.r * cap.r;

                    for (int it = 0; it < 3; it++)
                    {
                        float3 p = r + u * d;
                        float t = math.saturate(math.dot(p - cap.a, ab) / abLen2);
                        float3 dv = p - (cap.a + ab * t);
                        float sq = math.lengthsq(dv);
                        if (sq >= r2) break;

                        // distance along u until we leave the sphere around the closest point
                        float b = math.dot(dv, u);
                        float s = -b + math.sqrt(math.max(b * b + (r2 - sq), 0f));
                        d += s + 1e-5f;
                    }
                }
                if (d >= limit) break;
            }

            d = math.max(d0, math.min(d, limit));
            if (d > d0 + 1e-6f)
            {
                depth[i] = d;
                verts[i] = r + u * d;
                changed[0] = 1;
            }
        }
    }

    /// <summary>Cleanup: smooths carved areas but never lifts a vertex back out.</summary>
    [BurstCompile]
    struct DilateSmoothJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src;
        public NativeArray<float> dst;
        [ReadOnly] public NativeArray<int> start;
        [ReadOnly] public NativeArray<int> idx;
        public float strength;

        public void Execute(int i)
        {
            int s0 = start[i], s1 = start[i + 1];
            float d = src[i];
            if (s1 <= s0) { dst[i] = d; return; }

            float sum = 0f;
            for (int k = s0; k < s1; k++) sum += src[idx[k]];
            float avg = sum / (s1 - s0);
            dst[i] = math.max(d, math.lerp(d, avg, strength));
        }
    }

    [BurstCompile]
    struct ApplyDepthJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> rest;
        [ReadOnly] public NativeArray<float3> dir;
        [ReadOnly] public NativeArray<float> depth;
        public NativeArray<float3> verts;

        public void Execute(int i)
        {
            verts[i] = rest[i] + dir[i] * depth[i];
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
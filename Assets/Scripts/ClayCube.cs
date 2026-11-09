using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Hand-sculptable clay cube for Meta Quest 3 (standalone, passthrough).
/// Requires: Meta XR Core SDK, OVRCameraRig with OVRSkeleton on both hands,
/// Burst, Collections, Mathematics.
///
/// Attach to an empty GameObject with MeshFilter + MeshRenderer (URP Lit material).
/// Drag the left/right OVRSkeleton components into the fields below.
///
/// The whole hand (every finger segment + palm) is modelled as capsules that push the clay
/// out of their way. Vertices are only displaced while a capsule is inside them, so pulling
/// a finger back leaves the dent behind and nothing follows the hand.
///
/// Optional: enablePinchGrab lets a thumb+index pinch drag the clay (off by default).
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ClayCube : MonoBehaviour
{
    [Header("Mesh")]
    [Tooltip("Vertices per edge. 48 = ~13.8k verts, 64 = ~24.6k verts. Profile on device.")]
    public int resolution = 48;
    [Tooltip("Cube edge length in meters.")]
    public float size = 0.2f;

    [Header("Hands")]
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    [Header("Hand collision (meters)")]
    [Tooltip("Radius of the capsules along each finger segment.")]
    public float fingerRadius = 0.009f;
    [Tooltip("Radius of the capsules that make up the palm.")]
    public float palmRadius = 0.015f;

    [Header("Pinch grab (optional)")]
    [Tooltip("Off by default: a pinch can latch onto the clay when you retract your fingers.")]
    public bool enablePinchGrab = false;
    public float grabRadius = 0.04f;
    public float pinchOn = 0.020f;
    public float pinchOff = 0.035f;

    // ---- hand model: bone chains for fingers, links for the palm
    static readonly OVRSkeleton.BoneId[][] fingerChains =
    {
        new[] { OVRSkeleton.BoneId.Hand_Thumb1,  OVRSkeleton.BoneId.Hand_Thumb2,  OVRSkeleton.BoneId.Hand_Thumb3,  OVRSkeleton.BoneId.Hand_ThumbTip },
        new[] { OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Hand_Index2,  OVRSkeleton.BoneId.Hand_Index3,  OVRSkeleton.BoneId.Hand_IndexTip },
        new[] { OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Hand_Middle2, OVRSkeleton.BoneId.Hand_Middle3, OVRSkeleton.BoneId.Hand_MiddleTip },
        new[] { OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Hand_Ring2,   OVRSkeleton.BoneId.Hand_Ring3,   OVRSkeleton.BoneId.Hand_RingTip },
        new[] { OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Hand_Pinky2,  OVRSkeleton.BoneId.Hand_Pinky3,  OVRSkeleton.BoneId.Hand_PinkyTip },
    };

    // Wrist fans out to each knuckle, plus a line across the knuckles.
    static readonly OVRSkeleton.BoneId[,] palmLinks =
    {
        { OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.Hand_Thumb1 },
        { OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.Hand_Index1 },
        { OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.Hand_Middle1 },
        { OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.Hand_Ring1 },
        { OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.Hand_Pinky1 },
        { OVRSkeleton.BoneId.Hand_Index1,    OVRSkeleton.BoneId.Hand_Middle1 },
        { OVRSkeleton.BoneId.Hand_Middle1,   OVRSkeleton.BoneId.Hand_Ring1 },
        { OVRSkeleton.BoneId.Hand_Ring1,     OVRSkeleton.BoneId.Hand_Pinky1 },
    };

    struct Capsule
    {
        public float3 a;
        public float3 b;
        public float r;
    }

    Mesh mesh;
    NativeArray<float3> verts, normals;
    NativeArray<int> tris;
    NativeArray<Capsule> capsules;
    int capsuleCount;
    JobHandle handle;
    bool pending;

    // per-frame scratch for bone lookup (indexed by (int)BoneId)
    readonly Vector3[] bonePos = new Vector3[40];
    readonly bool[] boneHave = new bool[40];

    class GrabState
    {
        public bool active;
        public float3 last;
        public NativeArray<float> weights;
    }
    readonly GrabState grabLeft = new GrabState();
    readonly GrabState grabRight = new GrabState();

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        BuildWeldedCube(out var vList, out var tList);

        verts = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        normals = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        tris = new NativeArray<int>(tList.ToArray(), Allocator.Persistent);
        capsules = new NativeArray<Capsule>(64, Allocator.Persistent);
        grabLeft.weights = new NativeArray<float>(vList.Count, Allocator.Persistent);
        grabRight.weights = new NativeArray<float>(vList.Count, Allocator.Persistent);

        for (int i = 0; i < vList.Count; i++) verts[i] = vList[i];

        mesh = new Mesh { name = "ClayCube" };
        if (vList.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.SetVertices(vList);
        mesh.SetTriangles(tList, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    /// <summary>Builds a cube surface with one vertex per grid position (no duplicated seams).</summary>
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

    // ----------------------------------------------------------------- update

    void Update()
    {
        capsuleCount = 0;
        JobHandle dep = default;

        dep = ProcessHand(rightSkeleton, grabRight, dep);
        dep = ProcessHand(leftSkeleton, grabLeft, dep);

        if (capsuleCount == 0 && !grabLeft.active && !grabRight.active)
        {
            dep.Complete();
            return;
        }

        var poke = new PokeJob { verts = verts, capsules = capsules, capsuleCount = capsuleCount };
        var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
        dep = poke.Schedule(verts.Length, 256, dep);
        handle = norm.Schedule(dep);
        pending = true;
    }

    JobHandle ProcessHand(OVRSkeleton skel, GrabState grab, JobHandle dep)
    {
        if (skel == null || !skel.IsInitialized || !skel.IsDataValid || !skel.IsDataHighConfidence)
        {
            grab.active = false;
            return dep;
        }

        // Cache bone world positions by id.
        System.Array.Clear(boneHave, 0, boneHave.Length);
        var bones = skel.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            int idx = (int)bones[i].Id;
            if (idx >= 0 && idx < boneHave.Length)
            {
                bonePos[idx] = bones[i].Transform.position;
                boneHave[idx] = true;
            }
        }

        float scale = transform.lossyScale.x;

        // ---- optional pinch grab
        if (enablePinchGrab)
        {
            dep = HandlePinch(grab, dep, scale);
        }
        else
        {
            grab.active = false;
        }

        // ---- finger capsules (skip thumb + index while a pinch grab is active)
        for (int f = 0; f < fingerChains.Length; f++)
        {
            if (grab.active && f <= 1) continue;
            var chain = fingerChains[f];
            for (int k = 0; k < chain.Length - 1; k++)
                AddCapsule(chain[k], chain[k + 1], fingerRadius, scale);
        }

        // ---- palm capsules
        for (int p = 0; p < palmLinks.GetLength(0); p++)
        {
            float r = p >= 5 ? fingerRadius : palmRadius; // knuckle line is thinner
            AddCapsule(palmLinks[p, 0], palmLinks[p, 1], r, scale);
        }

        return dep;
    }

    void AddCapsule(OVRSkeleton.BoneId a, OVRSkeleton.BoneId b, float radiusWorld, float scale)
    {
        int ia = (int)a, ib = (int)b;
        if (ia >= boneHave.Length || ib >= boneHave.Length) return;
        if (!boneHave[ia] || !boneHave[ib]) return;
        if (capsuleCount >= capsules.Length) return;

        Vector3 la = transform.InverseTransformPoint(bonePos[ia]);
        Vector3 lb = transform.InverseTransformPoint(bonePos[ib]);
        capsules[capsuleCount++] = new Capsule { a = la, b = lb, r = radiusWorld / scale };
    }

    JobHandle HandlePinch(GrabState grab, JobHandle dep, float scale)
    {
        int it = (int)OVRSkeleton.BoneId.Hand_IndexTip;
        int tt = (int)OVRSkeleton.BoneId.Hand_ThumbTip;
        if (!boneHave[it] || !boneHave[tt])
        {
            grab.active = false;
            return dep;
        }

        float d = Vector3.Distance(bonePos[it], bonePos[tt]);
        bool pinching = grab.active ? d < pinchOff : d < pinchOn;
        float3 pinchLocal = transform.InverseTransformPoint((bonePos[it] + bonePos[tt]) * 0.5f);

        if (pinching && !grab.active)
        {
            var wJob = new GrabWeightsJob
            {
                verts = verts,
                weights = grab.weights,
                center = pinchLocal,
                radius = grabRadius / scale
            };
            dep = wJob.Schedule(verts.Length, 256, dep);
            grab.active = true;
            grab.last = pinchLocal;
        }
        else if (pinching && grab.active)
        {
            float3 delta = pinchLocal - grab.last;
            if (math.lengthsq(delta) > 0f)
            {
                var mJob = new GrabMoveJob { verts = verts, weights = grab.weights, delta = delta };
                dep = mJob.Schedule(verts.Length, 256, dep);
            }
            grab.last = pinchLocal;
        }
        else
        {
            grab.active = false;
        }
        return dep;
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
        if (normals.IsCreated) normals.Dispose();
        if (tris.IsCreated) tris.Dispose();
        if (capsules.IsCreated) capsules.Dispose();
        if (grabLeft.weights.IsCreated) grabLeft.weights.Dispose();
        if (grabRight.weights.IsCreated) grabRight.weights.Dispose();
    }

    // ------------------------------------------------------------------- jobs

    /// <summary>Pushes vertices out of every hand capsule (closest point on the segment, radial push).</summary>
    [BurstCompile]
    struct PokeJob : IJobParallelFor
    {
        public NativeArray<float3> verts;
        [ReadOnly] public NativeArray<Capsule> capsules;
        public int capsuleCount;

        public void Execute(int i)
        {
            float3 p = verts[i];
            for (int c = 0; c < capsuleCount; c++)
            {
                Capsule cap = capsules[c];
                float3 ab = cap.b - cap.a;
                float t = math.saturate(math.dot(p - cap.a, ab) / math.max(math.dot(ab, ab), 1e-8f));
                float3 closest = cap.a + ab * t;
                float3 d = p - closest;
                float sq = math.lengthsq(d);
                if (sq < cap.r * cap.r)
                {
                    float len = math.sqrt(sq);
                    d = len > 1e-6f ? d / len : new float3(0, 1, 0);
                    p = closest + d * cap.r;
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
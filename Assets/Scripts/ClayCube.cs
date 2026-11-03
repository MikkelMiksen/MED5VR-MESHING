using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Hand-sculptable clay cube for Meta Quest 3 (standalone, passthrough).
/// Requires: Meta XR Core SDK, OVRCameraRig with OVRHand/OVRSkeleton on both hands,
/// Burst, Collections, Mathematics.
///
/// Attach to an empty GameObject with MeshFilter + MeshRenderer (URP Lit material).
/// Drag the left/right OVRSkeleton components (from OVRLeftHandVisual / OVRRightHandVisual
/// or the OVRHandPrefab skeletons under the rig) into the fields below.
///
/// - Fingertips + wrist act as small spheres that push the surface (dents).
/// - Pinching (thumb + index) grabs the clay near the pinch point and drags it (pull/stretch).
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

    [Header("Poke (dents)")]
    public float fingerRadius = 0.010f;
    public float palmRadius = 0.030f;

    [Header("Pinch grab (pull / stretch)")]
    public float grabRadius = 0.04f;
    [Tooltip("Pinch starts when thumb-index tip distance (m) drops below this.")]
    public float pinchOn = 0.020f;
    [Tooltip("Pinch ends when distance rises above this (hysteresis).")]
    public float pinchOff = 0.035f;

    Mesh mesh;
    NativeArray<float3> verts, normals;
    NativeArray<int> tris;
    NativeArray<float4> tips;      // xyz = local position, w = local radius
    int tipCount;
    JobHandle handle;
    bool pending;

    class GrabState
    {
        public bool active;
        public float3 last;
        public NativeArray<float> weights;
    }
    GrabState grabLeft = new GrabState();
    GrabState grabRight = new GrabState();

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        BuildWeldedCube(out var vList, out var tList);

        verts = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        normals = new NativeArray<float3>(vList.Count, Allocator.Persistent);
        tris = new NativeArray<int>(tList.ToArray(), Allocator.Persistent);
        tips = new NativeArray<float4>(16, Allocator.Persistent);
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
        tipCount = 0;
        JobHandle dep = default;

        dep = ProcessHand(rightSkeleton, grabRight, dep);
        dep = ProcessHand(leftSkeleton, grabLeft, dep);

        // Nothing tracked and nothing grabbed -> nothing to do.
        if (tipCount == 0 && !grabLeft.active && !grabRight.active)
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

    JobHandle ProcessHand(OVRSkeleton skel, GrabState grab, JobHandle dep)
    {
        if (skel == null || !skel.IsInitialized || !skel.IsDataValid || !skel.IsDataHighConfidence)
        {
            grab.active = false;
            return dep;
        }

        var bones = skel.Bones;
        float scale = transform.lossyScale.x;

        Transform thumbTip = null, indexTip = null;
        for (int i = 0; i < bones.Count; i++)
        {
            var id = bones[i].Id;
            if (id == OVRSkeleton.BoneId.Hand_ThumbTip) thumbTip = bones[i].Transform;
            else if (id == OVRSkeleton.BoneId.Hand_IndexTip) indexTip = bones[i].Transform;
        }

        // ---- pinch state (with hysteresis)
        bool pinchingNow = false;
        float3 pinchLocal = float3.zero;
        if (thumbTip != null && indexTip != null)
        {
            float d = Vector3.Distance(thumbTip.position, indexTip.position);
            if (!grab.active && d < pinchOn) pinchingNow = true;
            else if (grab.active && d < pinchOff) pinchingNow = true;

            Vector3 mid = (thumbTip.position + indexTip.position) * 0.5f;
            pinchLocal = transform.InverseTransformPoint(mid);
        }

        if (pinchingNow && !grab.active)
        {
            // Grab begins: compute per-vertex weights around the pinch point.
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
        else if (pinchingNow && grab.active)
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

        // ---- poke spheres (skip thumb/index tips while pinching so the grab isn't fought)
        for (int i = 0; i < bones.Count; i++)
        {
            var id = bones[i].Id;
            bool isTip = id == OVRSkeleton.BoneId.Hand_ThumbTip
                      || id == OVRSkeleton.BoneId.Hand_IndexTip
                      || id == OVRSkeleton.BoneId.Hand_MiddleTip
                      || id == OVRSkeleton.BoneId.Hand_RingTip
                      || id == OVRSkeleton.BoneId.Hand_PinkyTip;
            bool isPalm = id == OVRSkeleton.BoneId.Hand_WristRoot;
            if (!isTip && !isPalm) continue;

            if (grab.active && (id == OVRSkeleton.BoneId.Hand_ThumbTip || id == OVRSkeleton.BoneId.Hand_IndexTip))
                continue;

            if (tipCount >= tips.Length) break;

            Vector3 local = transform.InverseTransformPoint(bones[i].Transform.position);
            float r = (isPalm ? palmRadius : fingerRadius) / scale;
            tips[tipCount++] = new float4(local.x, local.y, local.z, r);
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
        if (tips.IsCreated) tips.Dispose();
        if (grabLeft.weights.IsCreated) grabLeft.weights.Dispose();
        if (grabRight.weights.IsCreated) grabRight.weights.Dispose();
    }

    // ------------------------------------------------------------------- jobs

    /// <summary>Pushes vertices out of fingertip/palm spheres.</summary>
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

    /// <summary>Smoothstep falloff weights around the pinch point, captured once at grab start.</summary>
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

    /// <summary>Drags grabbed vertices along with the pinch movement.</summary>
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

    /// <summary>Area-weighted vertex normals (single-threaded; fine for ~25k verts under Burst).</summary>
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

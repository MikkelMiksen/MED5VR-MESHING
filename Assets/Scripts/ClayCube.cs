using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Hand-carved clay cube for Meta Quest 3 (standalone, passthrough).
/// Requires: Meta XR Core SDK, OVRCameraRig with OVRSkeleton on both hands,
/// Burst, Collections, Mathematics.
///
/// How it works
///  - Every vertex has a fixed "inward" direction (the reverse of its original surface normal)
///    and a depth >= 0 along it. Hands can only INCREASE depth, so clay can only be pushed in,
///    never pulled out, no matter how fast the hand moves.
///  - The whole hand (every finger segment + palm) is a set of capsules. Fast hand movement is
///    swept in sub-steps so the hand can't skip over vertices.
///  - A stroke starts when the hand enters the cube's bounds and ends shortly after it leaves.
///    At stroke end the carved area gets a light cleanup pass and the pre-stroke state is
///    stored for undo.
///
/// Undo:
///  - Left hand: pinch thumb + middle finger and hold ~0.35 s, with that hand away from the cube
///  - Left controller: X button
///  - Or call Undo() from your own UI button
///
/// Tip: make the cube a bit bigger than the real object, place it around/over it, then press
/// your hand against the real object. The clay gets carved down to the object's shape.
/// Use a uniform scale on the transform.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ClayCube : MonoBehaviour
{
    [Header("Mesh")]
    [Tooltip("Vertices per edge. 48 = ~13.8k verts, 64 = ~24.6k verts. Profile on device.")]
    public int resolution = 48;
    [Tooltip("Cube edge length in meters (before transform scale).")]
    public float size = 0.2f;

    [Header("Hands")]
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    [Header("Hand collision (meters)")]
    public float fingerRadius = 0.009f;
    public float palmRadius = 0.015f;
    [Tooltip("Max extra capsule samples per frame when the hand moves fast.")]
    public int maxSubSteps = 4;

    [Header("Carving")]
    [Tooltip("Clay can't be pushed closer than this (meters) to the cube's centre. Prevents the surface folding through itself.")]
    public float coreRadius = 0.03f;
    [Tooltip("Cleanup passes applied after each stroke. 0 = off.")]
    public int smoothIterations = 1;
    [Range(0f, 1f)] public float smoothStrength = 0.35f;
    [Tooltip("Seconds without the hand near the cube before a stroke is considered finished.")]
    public float strokeEndDelay = 0.3f;

    [Header("Undo")]
    public int maxUndo = 15;
    public float undoHoldTime = 0.35f;
    [Tooltip("Left hand must be at least this far (meters) from the cube for the undo pinch to count.")]
    public float undoMinHandDistance = 0.12f;
    public float undoPinchOn = 0.020f;
    public float undoPinchOff = 0.035f;

    // ---- hand model
    static readonly OVRSkeleton.BoneId[][] fingerChains =
    {
        new[] { OVRSkeleton.BoneId.Hand_Thumb1,  OVRSkeleton.BoneId.Hand_Thumb2,  OVRSkeleton.BoneId.Hand_Thumb3,  OVRSkeleton.BoneId.Hand_ThumbTip },
        new[] { OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Hand_Index2,  OVRSkeleton.BoneId.Hand_Index3,  OVRSkeleton.BoneId.Hand_IndexTip },
        new[] { OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Hand_Middle2, OVRSkeleton.BoneId.Hand_Middle3, OVRSkeleton.BoneId.Hand_MiddleTip },
        new[] { OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Hand_Ring2,   OVRSkeleton.BoneId.Hand_Ring3,   OVRSkeleton.BoneId.Hand_RingTip },
        new[] { OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Hand_Pinky2,  OVRSkeleton.BoneId.Hand_Pinky3,  OVRSkeleton.BoneId.Hand_PinkyTip },
    };

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

    class HandState
    {
        public readonly Vector3[] prev = new Vector3[40];
        public readonly bool[] prevHave = new bool[40];
        public bool prevValid;
        public bool tracked;
        public Vector3 wrist;
        public float thumbMiddle = 1f;
    }

    // ---- mesh + clay data
    Mesh mesh;
    NativeArray<float3> rest, dir, verts, normals;
    NativeArray<float> depth, depthTmp;
    NativeArray<int> tris, neighborStart, neighborIdx;
    NativeArray<Capsule> capsules;
    NativeArray<int> changed;
    int capsuleCount;
    JobHandle handle;
    bool pending;

    // ---- stroke / undo
    bool inStroke, strokeChanged;
    float idleTime;
    float[] strokeSnapshot;
    readonly List<float[]> undoStack = new List<float[]>();
    float undoTimer;
    bool undoFired;

    // ---- per-frame scratch
    readonly HandState handL = new HandState();
    readonly HandState handR = new HandState();
    readonly Vector3[] bonePos = new Vector3[40];
    readonly bool[] boneHave = new bool[40];
    readonly Vector3[] localPos = new Vector3[40];

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        BuildWeldedCube(out var restList, out var normalList, out var tList);
        int count = restList.Count;

        rest = new NativeArray<float3>(count, Allocator.Persistent);
        dir = new NativeArray<float3>(count, Allocator.Persistent);
        verts = new NativeArray<float3>(count, Allocator.Persistent);
        normals = new NativeArray<float3>(count, Allocator.Persistent);
        depth = new NativeArray<float>(count, Allocator.Persistent);
        depthTmp = new NativeArray<float>(count, Allocator.Persistent);
        tris = new NativeArray<int>(tList.ToArray(), Allocator.Persistent);
        capsules = new NativeArray<Capsule>(256, Allocator.Persistent);
        changed = new NativeArray<int>(1, Allocator.Persistent);

        for (int i = 0; i < count; i++)
        {
            rest[i] = restList[i];
            verts[i] = restList[i];
            dir[i] = -math.normalize((float3)restList[i]);   // radially toward the centre: surface stays star-shaped, can't fold
        }

        BuildNeighbors(count, tList);

        mesh = new Mesh { name = "ClayCube" };
        if (count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        mesh.SetVertices(restList);
        mesh.SetTriangles(tList, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds(); // vertices only move inward, so these bounds stay valid
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    void BuildWeldedCube(out List<Vector3> restList, out List<Vector3> normalList, out List<int> tList)
    {
        int n = resolution, s = n + 1;
        var map = new int[s * s * s];
        System.Array.Fill(map, -1);
        var vl = new List<Vector3>();
        var nl = new List<Vector3>();
        var tl = new List<int>();

        int Get(int x, int y, int z)
        {
            int k = x + s * (y + s * z);
            if (map[k] < 0)
            {
                map[k] = vl.Count;
                vl.Add((new Vector3(x, y, z) / n - Vector3.one * 0.5f) * size);
                // Face interior: face normal. Edges/corners: average of the touching faces.
                nl.Add(new Vector3(
                    x == 0 ? -1f : (x == n ? 1f : 0f),
                    y == 0 ? -1f : (y == n ? 1f : 0f),
                    z == 0 ? -1f : (z == n ? 1f : 0f)).normalized);
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
        normalList = nl;
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

    // ----------------------------------------------------------------- update

    void Update()
    {
        float dt = Time.deltaTime;
        capsuleCount = 0;

        ProcessHand(rightSkeleton, handR);
        ProcessHand(leftSkeleton, handL);

        UpdateUndoGesture(dt);
        if (OVRInput.GetDown(OVRInput.Button.Three)) Undo(); // left controller X

        if (capsuleCount > 0)
        {
            idleTime = 0f;
            if (!inStroke) BeginStroke();

            float scale = transform.lossyScale.x;
            var carve = new CarveJob
            {
                rest = rest,
                dir = dir,
                depth = depth,
                verts = verts,
                capsules = capsules,
                capsuleCount = capsuleCount,
                core = coreRadius / scale,
                changed = changed
            };
            var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
            handle = norm.Schedule(carve.Schedule(verts.Length, 256));
            pending = true;
        }
        else if (inStroke)
        {
            idleTime += dt;
            if (idleTime > strokeEndDelay) EndStroke();
        }
    }

    void LateUpdate()
    {
        FinishPending();
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

    // ------------------------------------------------------------ hand -> capsules

    void ProcessHand(OVRSkeleton skel, HandState hs)
    {
        hs.tracked = false;
        if (skel == null || !skel.IsInitialized || !skel.IsDataValid || !skel.IsDataHighConfidence)
        {
            hs.prevValid = false;
            return;
        }

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

        hs.tracked = true;
        int wr = (int)OVRSkeleton.BoneId.Hand_WristRoot;
        int tt = (int)OVRSkeleton.BoneId.Hand_ThumbTip;
        int mt = (int)OVRSkeleton.BoneId.Hand_MiddleTip;
        if (boneHave[wr]) hs.wrist = bonePos[wr];
        hs.thumbMiddle = (boneHave[tt] && boneHave[mt]) ? Vector3.Distance(bonePos[tt], bonePos[mt]) : 1f;

        float scale = transform.lossyScale.x;

        // How far did the hand move since last frame? Sweep fast movement in sub-steps.
        float maxMove = 0f;
        if (hs.prevValid)
        {
            for (int i = 0; i < boneHave.Length; i++)
                if (boneHave[i] && hs.prevHave[i])
                    maxMove = Mathf.Max(maxMove, Vector3.Distance(bonePos[i], hs.prev[i]));
        }

        int steps = 1;
        if (hs.prevValid && maxMove < 0.5f) // ignore tracking jumps
            steps = Mathf.Clamp(Mathf.CeilToInt(maxMove / (fingerRadius * 0.8f)), 1, Mathf.Max(1, maxSubSteps));

        for (int s = 1; s <= steps; s++)
        {
            float t = s / (float)steps;
            for (int i = 0; i < boneHave.Length; i++)
            {
                if (!boneHave[i]) continue;
                Vector3 w = (hs.prevValid && hs.prevHave[i]) ? Vector3.Lerp(hs.prev[i], bonePos[i], t) : bonePos[i];
                localPos[i] = transform.InverseTransformPoint(w);
            }
            BuildHandCapsules(scale);
        }

        // remember this frame for the next one
        for (int i = 0; i < boneHave.Length; i++)
        {
            hs.prevHave[i] = boneHave[i];
            hs.prev[i] = bonePos[i];
        }
        hs.prevValid = true;
    }

    void BuildHandCapsules(float scale)
    {
        for (int f = 0; f < fingerChains.Length; f++)
        {
            var chain = fingerChains[f];
            for (int k = 0; k < chain.Length - 1; k++)
                AddCapsule(chain[k], chain[k + 1], fingerRadius, scale);
        }

        for (int p = 0; p < palmLinks.GetLength(0); p++)
        {
            float r = p >= 5 ? fingerRadius : palmRadius; // knuckle line is thinner
            AddCapsule(palmLinks[p, 0], palmLinks[p, 1], r, scale);
        }
    }

    void AddCapsule(OVRSkeleton.BoneId a, OVRSkeleton.BoneId b, float radiusWorld, float scale)
    {
        int ia = (int)a, ib = (int)b;
        if (ia >= boneHave.Length || ib >= boneHave.Length) return;
        if (!boneHave[ia] || !boneHave[ib]) return;
        if (capsuleCount >= capsules.Length) return;

        Vector3 la = localPos[ia], lb = localPos[ib];
        float r = radiusWorld / scale;

        // Cull capsules that can't touch the cube (vertices only ever lie inside its bounds).
        float half = size * 0.5f + r;
        if (Mathf.Min(la.x, lb.x) > half || Mathf.Max(la.x, lb.x) < -half) return;
        if (Mathf.Min(la.y, lb.y) > half || Mathf.Max(la.y, lb.y) < -half) return;
        if (Mathf.Min(la.z, lb.z) > half || Mathf.Max(la.z, lb.z) < -half) return;

        capsules[capsuleCount++] = new Capsule { a = la, b = lb, r = r };
    }

    // ------------------------------------------------------------ strokes + undo

    void BeginStroke()
    {
        strokeSnapshot = depth.ToArray();
        strokeChanged = false;
        inStroke = true;
        idleTime = 0f;
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

    /// <summary>Move the cube somewhere (e.g. onto a real object) and reset the clay.</summary>
    public void SpawnAt(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        ResetClay();
    }

    void RebuildMesh()
    {
        var apply = new ApplyDepthJob { rest = rest, dir = dir, depth = depth, verts = verts };
        var norm = new NormalsJob { verts = verts, tris = tris, normals = normals };
        norm.Schedule(apply.Schedule(verts.Length, 256)).Complete();
        UploadMesh();
    }

    void UpdateUndoGesture(float dt)
    {
        var h = handL;
        bool near = h.tracked && DistanceToCube(h.wrist) < undoMinHandDistance;

        if (h.tracked && !near && h.thumbMiddle < undoPinchOn)
        {
            undoTimer += dt;
            if (!undoFired && undoTimer >= undoHoldTime)
            {
                undoFired = true;
                Undo();
            }
        }
        else if (!h.tracked || near || h.thumbMiddle > undoPinchOff)
        {
            undoTimer = 0f;
            undoFired = false;
        }
    }

    float DistanceToCube(Vector3 worldPoint)
    {
        float3 p = transform.InverseTransformPoint(worldPoint);
        float3 q = math.abs(p) - size * 0.5f;
        return math.length(math.max(q, 0f)) * transform.lossyScale.x;
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

    /// <summary>
    /// Push-in-only carving. Each vertex slides along its fixed inward direction until it is
    /// outside every hand capsule. Depth never decreases.
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
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// Step 4: turns the TSDF volume into a live triangle mesh.
/// Runs surface nets on the GPU a couple of times per second, reads the result back asynchronously
/// and builds a normal Unity Mesh, so it works with any material, render pipeline and stereo mode.
/// The mesh is in world space (the mesh object stays at the origin).
public class SurfaceMesher : MonoBehaviour
{
    [Header("References")]
    public TSDFVolume volume;
    public ComputeShader compute;
    public Material material;          // use a semi-transparent material so you can see the real object

    [Header("Meshing")]
    public float meshInterval = 0.5f;  // seconds between re-meshes
    public float minWeight = 2f;       // voxels seen fewer times than this are ignored
    public int maxVertices = 200000;
    public int maxTriangles = 400000;
    public bool logStats = true;

    public Mesh Mesh { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex { public Vector3 position; public Vector3 normal; }

    static readonly VertexAttributeDescriptor[] Layout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
    };
    const MeshUpdateFlags NoChecks = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

    int kVertices, kQuads;
    ComputeBuffer cellIndex, verts, tris, counters;
    NativeArray<Vertex> vertCopy;
    NativeArray<uint> triCopy;
    bool allocated, pending;
    int generation;
    int lastVersion = -1;
    float nextTime;
    GameObject meshObject;
    readonly uint[] zeros = new uint[2];

    void Awake()
    {
        kVertices = compute.FindKernel("Vertices");
        kQuads = compute.FindKernel("Quads");

        Mesh = new Mesh { name = "ScanMesh", indexFormat = IndexFormat.UInt32 };
        Mesh.MarkDynamic();

        meshObject = new GameObject("ScanMesh");
        meshObject.AddComponent<MeshFilter>().sharedMesh = Mesh;
        var mr = meshObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        if (material == null) Debug.LogWarning("SurfaceMesher: assign a material, otherwise the mesh renders pink.");
    }

    void OnDestroy()
    {
        Release();
        if (meshObject != null) Destroy(meshObject);
        if (Mesh != null) Destroy(Mesh);
    }

    void Update()
    {
        bool active = volume != null && volume.Active;
        if (!active)
        {
            if (allocated) Release();
            return;
        }

        if (!allocated) Allocate();

        if (pending || Time.time < nextTime) return;
        if (volume.Version == lastVersion) return;   // nothing new has been fused since last mesh

        nextTime = Time.time + meshInterval;
        lastVersion = volume.Version;
        Extract();
    }

    void Allocate()
    {
        Vector3Int r = volume.Resolution;
        cellIndex = new ComputeBuffer(r.x * r.y * r.z, sizeof(int));
        verts = new ComputeBuffer(maxVertices, Marshal.SizeOf<Vertex>());
        tris = new ComputeBuffer(maxTriangles, sizeof(uint) * 3);
        counters = new ComputeBuffer(2, sizeof(uint));
        vertCopy = new NativeArray<Vertex>(maxVertices, Allocator.Persistent);
        triCopy = new NativeArray<uint>(maxTriangles * 3, Allocator.Persistent);
        generation++;
        lastVersion = -1;
        pending = false;
        allocated = true;
    }

    void Release()
    {
        if (!allocated) return;
        AsyncGPUReadback.WaitAllRequests();
        cellIndex.Release();
        verts.Release();
        tris.Release();
        counters.Release();
        vertCopy.Dispose();
        triCopy.Dispose();
        allocated = false;
        pending = false;
        if (Mesh != null) Mesh.Clear();
    }

    void Extract()
    {
        Vector3Int r = volume.Resolution;
        counters.SetData(zeros);

        compute.SetInts("_Res", r.x, r.y, r.z);
        compute.SetMatrix("_VoxelToWorld", volume.VoxelToWorld);
        compute.SetFloat("_MinWeight", minWeight);
        compute.SetInt("_MaxVerts", maxVertices);
        compute.SetInt("_MaxTris", maxTriangles);

        foreach (int k in new[] { kVertices, kQuads })
        {
            compute.SetTexture(k, "_Volume", volume.Volume);
            compute.SetBuffer(k, "_CellIndex", cellIndex);
            compute.SetBuffer(k, "_Verts", verts);
            compute.SetBuffer(k, "_Counters", counters);
        }
        compute.SetBuffer(kQuads, "_Tris", tris);

        int gx = Mathf.CeilToInt(r.x / 8f), gy = Mathf.CeilToInt(r.y / 8f), gz = Mathf.CeilToInt(r.z / 8f);
        compute.Dispatch(kVertices, gx, gy, gz);
        compute.Dispatch(kQuads, gx, gy, gz);

        pending = true;
        int gen = generation;
        AsyncGPUReadback.Request(counters, req => OnCounters(req, gen));
    }

    void OnCounters(AsyncGPUReadbackRequest req, int gen)
    {
        if (gen != generation || !allocated) return;
        if (req.hasError) { pending = false; return; }

        var c = req.GetData<uint>();
        int nv = (int)System.Math.Min(c[0], (uint)maxVertices);
        int nt = (int)System.Math.Min(c[1], (uint)maxTriangles);
        if (logStats) Debug.Log($"Meshing pass: {c[0]} vertices, {c[1]} triangles");
        if (nv == 0 || nt == 0)
        {
            Mesh.Clear();
            pending = false;
            return;
        }

        int got = 0;
        AsyncGPUReadback.Request(verts, nv * Marshal.SizeOf<Vertex>(), 0, r =>
        {
            if (gen != generation || !allocated) return;
            if (r.hasError) { pending = false; return; }
            NativeArray<Vertex>.Copy(r.GetData<Vertex>(), vertCopy, nv);
            if (++got == 2) Apply(nv, nt);
        });
        AsyncGPUReadback.Request(tris, nt * sizeof(uint) * 3, 0, r =>
        {
            if (gen != generation || !allocated) return;
            if (r.hasError) { pending = false; return; }
            NativeArray<uint>.Copy(r.GetData<uint>(), triCopy, nt * 3);
            if (++got == 2) Apply(nv, nt);
        });
    }

    void Apply(int nv, int nt)
    {
        Mesh.Clear();
        Mesh.indexFormat = IndexFormat.UInt32;
        Mesh.SetVertexBufferParams(nv, Layout);
        Mesh.SetVertexBufferData(vertCopy, 0, 0, nv, 0, NoChecks);
        Mesh.SetIndexBufferParams(nt * 3, IndexFormat.UInt32);
        Mesh.SetIndexBufferData(triCopy, 0, 0, nt * 3, NoChecks);
        Mesh.subMeshCount = 1;
        Mesh.SetSubMesh(0, new SubMeshDescriptor(0, nt * 3), NoChecks);
        Mesh.RecalculateBounds();
        pending = false;

        if (logStats) Debug.Log($"Mesh updated: {nv} vertices, {nt} triangles");
    }
}
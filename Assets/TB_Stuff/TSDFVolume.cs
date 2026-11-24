using UnityEngine;
using UnityEngine.Rendering;

/// Step 3: owns the TSDF volume and runs the fusion kernel.
/// Locking the ScanBounds box starts a scan (allocates and clears the volume).
/// Unlocking the box discards the volume, because moving the box invalidates it.
public class TSDFVolume : MonoBehaviour
{
    [Header("References")]
    public ScanBounds bounds;
    public DepthSource depth;
    public ComputeShader compute;
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    [Header("Fusion")]
    [Range(1, 30)] public float updatesPerSecond = 15f;
    public float truncationInVoxels = 4f;
    public float maxWeight = 64f;
    public float minRange = 0.3f;
    public float maxRange = 1.5f;
    [Range(0.01f, 0.2f)] public float edgeThreshold = 0.05f;
    public bool flipY = false;   // flip if the scan looks upside-down

    [Header("Hand masking")]
    public float handRadius = 0.08f;       // wrist and knuckle
    public float fingertipRadius = 0.05f;

    [Header("Debug")]
    public bool logStats = true;

    public RenderTexture Volume { get; private set; }
    public Vector3Int Resolution { get; private set; }
    public float VoxelSize { get; private set; }
    public float Truncation { get; private set; }
    public Matrix4x4 VoxelToWorld { get; private set; }
    public bool Active { get; private set; }

    int kClear, kIntegrate, kCount;
    ComputeBuffer counters;
    bool countPending;
    float nextFuseTime, nextStatsTime;
    readonly Vector4[] handSpheres = new Vector4[16];

    void Awake()
    {
        kClear = compute.FindKernel("Clear");
        kIntegrate = compute.FindKernel("Integrate");
        kCount = compute.FindKernel("Count");
    }

    void OnDisable() { End(); }

    void Update()
    {
        bool shouldScan = bounds != null && bounds.Locked;
        if (shouldScan && !Active) Begin();
        else if (!shouldScan && Active) End();
        if (!Active) return;

        if (Time.time < nextFuseTime || !depth.IsReady) return;
        nextFuseTime = Time.time + 1f / updatesPerSecond;
        Fuse();

        if (logStats && Time.time >= nextStatsTime && !countPending)
        {
            nextStatsTime = Time.time + 1f;
            RequestStats();
        }
    }

    void Begin()
    {
        bounds.GetGrid(out Vector3Int res, out float voxelSize, out Matrix4x4 v2w);
        Resolution = res;
        VoxelSize = voxelSize;
        VoxelToWorld = v2w;
        Truncation = truncationInVoxels * voxelSize;

        Volume = new RenderTexture(res.x, res.y, 0, RenderTextureFormat.RGHalf)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = res.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        Volume.Create();

        counters = new ComputeBuffer(2, sizeof(uint));

        SetCommon(kClear);
        compute.SetTexture(kClear, "_Volume", Volume);
        Dispatch(kClear);

        depth.SetCapturing(true);
        Active = true;
        Debug.Log($"TSDF scan started: {res.x}x{res.y}x{res.z} voxels, voxel size {voxelSize * 1000f:F1} mm, " +
                  $"truncation {Truncation * 1000f:F0} mm");
    }

    void End()
    {
        if (!Active && Volume == null) return;
        Active = false;
        if (depth != null) depth.SetCapturing(false);
        AsyncGPUReadback.WaitAllRequests();
        countPending = false;
        if (counters != null) { counters.Release(); counters = null; }
        if (Volume != null) { Volume.Release(); Destroy(Volume); Volume = null; }
    }

    void SetCommon(int kernel)
    {
        compute.SetInts("_Res", Resolution.x, Resolution.y, Resolution.z);
        compute.SetMatrix("_VoxelToWorld", VoxelToWorld);
    }

    void Dispatch(int kernel)
    {
        compute.Dispatch(kernel,
            Mathf.CeilToInt(Resolution.x / 8f),
            Mathf.CeilToInt(Resolution.y / 8f),
            Mathf.CeilToInt(Resolution.z / 8f));
    }

    void Fuse()
    {
        if (!depth.BindToCompute(compute, kIntegrate)) return;

        SetCommon(kIntegrate);
        compute.SetTexture(kIntegrate, "_Volume", Volume);
        compute.SetFloat("_Truncation", Truncation);
        compute.SetFloat("_MaxWeight", maxWeight);
        compute.SetFloat("_MinRange", minRange);
        compute.SetFloat("_MaxRange", maxRange);
        compute.SetFloat("_EdgeThreshold", edgeThreshold);
        compute.SetInt("_DepthSize", depth.DepthTexture.width);
        compute.SetInt("_FlipY", flipY ? 1 : 0);

        int hands = GatherHandSpheres();
        compute.SetInt("_HandCount", hands);
        compute.SetVectorArray("_HandSpheres", handSpheres);

        Dispatch(kIntegrate);
    }

    // ---- hand masking -------------------------------------------------------------------

    int GatherHandSpheres()
    {
        Matrix4x4 worldToBox = bounds.transform.worldToLocalMatrix;
        Vector3 half = bounds.size * 0.5f;
        int n = AddHand(leftSkeleton, 0, worldToBox, half);
        n = AddHand(rightSkeleton, n, worldToBox, half);
        return n;
    }

    int AddHand(OVRSkeleton skeleton, int n, Matrix4x4 worldToBox, Vector3 half)
    {
        if (skeleton == null || !skeleton.IsInitialized || !skeleton.IsDataValid) return n;

        foreach (var bone in skeleton.Bones)
        {
            float r;
            switch (bone.Id)
            {
                case OVRSkeleton.BoneId.Hand_WristRoot:
                case OVRSkeleton.BoneId.Hand_Middle1:
                    r = handRadius; break;
                case OVRSkeleton.BoneId.Hand_ThumbTip:
                case OVRSkeleton.BoneId.Hand_IndexTip:
                case OVRSkeleton.BoneId.Hand_MiddleTip:
                case OVRSkeleton.BoneId.Hand_RingTip:
                case OVRSkeleton.BoneId.Hand_PinkyTip:
                    r = fingertipRadius; break;
                default: continue;
            }

            Vector3 p = bone.Transform.position;
            Vector3 l = worldToBox.MultiplyPoint3x4(p);
            if (Mathf.Abs(l.x) > half.x + r || Mathf.Abs(l.y) > half.y + r || Mathf.Abs(l.z) > half.z + r)
                continue;   // too far from the box to matter

            if (n >= handSpheres.Length) return n;
            handSpheres[n++] = new Vector4(p.x, p.y, p.z, r);
        }
        return n;
    }

    // ---- debug stats --------------------------------------------------------------------

    void RequestStats()
    {
        counters.SetData(new uint[2]);
        SetCommon(kCount);
        compute.SetTexture(kCount, "_Volume", Volume);
        compute.SetBuffer(kCount, "_Counters", counters);
        Dispatch(kCount);

        countPending = true;
        long total = (long)Resolution.x * Resolution.y * Resolution.z;
        AsyncGPUReadback.Request(counters, req =>
        {
            countPending = false;
            if (req.hasError) return;
            var d = req.GetData<uint>();
            Debug.Log($"TSDF: observed {d[0]} / {total} voxels ({100f * d[0] / total:F1}%), " +
                      $"near surface: {d[1]}");
        });
    }
}
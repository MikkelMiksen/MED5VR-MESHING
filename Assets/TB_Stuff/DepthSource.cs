using System.Collections.Generic;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

/// Step 2: wraps Meta's EnvironmentDepthManager and exposes what the TSDF fusion kernel needs.
/// The depth manager costs GPU time whenever it is enabled, so it stays OFF until SetCapturing(true)
/// is called (TSDFVolume does this while a scan is running).
/// Untick the EnvironmentDepthManager component in the Inspector so it starts disabled.
[RequireComponent(typeof(EnvironmentDepthManager))]
public class DepthSource : MonoBehaviour
{
    public bool removeHands = true;
    public bool logOnceWhenReady = true;

    static readonly int DepthTexId     = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int ReprojId       = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int ZBufferParamId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

    EnvironmentDepthManager manager;
    readonly List<Matrix4x4> matrixScratch = new List<Matrix4x4>(2);
    bool capturing;
    bool logged;

    public bool IsReady { get; private set; }
    public string Status { get; private set; } = "off";   // why depth is or isn't ready, shown in the headset
    public Texture DepthTexture { get; private set; }          // two slices: left eye, right eye
    public Matrix4x4[] ReprojectionMatrices { get; } = new Matrix4x4[2];
    public Vector4 ZBufferParams { get; private set; }

    void Awake()
    {
        manager = GetComponent<EnvironmentDepthManager>();
        manager.enabled = false;
    }

    public void SetCapturing(bool on)
    {
        if (on == capturing) return;
        capturing = on;
        IsReady = false;

        if (on)
        {
            if (!EnvironmentDepthManager.IsSupported)
            {
                Status = "NOT SUPPORTED on this device/build";
                Debug.LogWarning("DepthSource: environment depth is not supported on this device/build.");
                capturing = false;
                return;
            }
            // We do our own fusion, so we don't want the occlusion keyword work.
            manager.OcclusionShadersMode = OcclusionShadersMode.None;
            manager.RemoveHands = removeHands;
            manager.enabled = true;
        }
        else
        {
            manager.enabled = false;
        }
    }

    void OnDisable()
    {
        if (capturing) SetCapturing(false);
    }

    void Update()
    {
        if (!capturing) { IsReady = false; if (Status != "NOT SUPPORTED on this device/build") Status = "off"; return; }
        IsReady = false;
        if (!manager.IsDepthAvailable)
        {
            Status = "manager: no depth frame yet";
            return;
        }

        DepthTexture = Shader.GetGlobalTexture(DepthTexId);
        Shader.GetGlobalMatrixArray(ReprojId, matrixScratch);   // no per-frame allocation
        ZBufferParams = Shader.GetGlobalVector(ZBufferParamId);

        if (DepthTexture == null) { Status = "depth texture is null"; return; }
        if (matrixScratch.Count < 2) { Status = "matrices missing (count " + matrixScratch.Count + ")"; return; }
        ReprojectionMatrices[0] = matrixScratch[0];
        ReprojectionMatrices[1] = matrixScratch[1];
        IsReady = true;
        Status = "ready";

        if (logOnceWhenReady && !logged)
        {
            logged = true;
            Debug.Log($"DepthSource ready. Texture {DepthTexture.width}x{DepthTexture.height} " +
                      $"({DepthTexture.dimension}), zBufferParams {ZBufferParams}");
        }
    }

    /// Call once per frame before dispatching the fusion kernel.
    /// Compute shaders do not see global textures automatically, so the texture is
    /// bound with SetTextureFromGlobal, and the matrices/params are passed explicitly.
    public bool BindToCompute(ComputeShader cs, int kernel)
    {
        if (!IsReady) return false;
        cs.SetTextureFromGlobal(kernel, "_DepthTex", "_EnvironmentDepthTexture");
        cs.SetMatrixArray("_DepthReprojection", ReprojectionMatrices);
        cs.SetVector("_DepthZBufferParams", ZBufferParams);
        return true;
    }
}
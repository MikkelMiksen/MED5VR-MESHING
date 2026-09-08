using UnityEngine;
using Meta.XR.EnvironmentDepth; // Meta XR Core SDK, package com.meta.xr.sdk.core

/// <summary>
/// Toggles a raw, unprocessed view of the Quest 3 Depth API output.
/// Press A to activate, press A again to deactivate.
///
/// Setup required in the scene:
/// 1. An EnvironmentDepthManager component somewhere in the scene
///    (Meta XR Core SDK, Scripts/EnvironmentDepth). Assign it below.
/// 2. Passthrough enabled (OVRPassthroughLayer on the camera rig).
/// 3. OVRManager -> Quest Features -> Scene Support set to "Required"
///    or "Supported" (this is what grants the depth/spatial permission).
/// 4. A quad (or any mesh) placed in front of the camera, with a material
///    using the RawEnvironmentDepth shader (see RawEnvironmentDepth.shader).
///    Assign that quad's GameObject below and leave it disabled by default.
/// 5. Graphics API = Vulkan, Stereo Rendering Mode = Multiview
///    (the Meta XR Project Setup Tool will flag this if it is wrong).
/// </summary>
public class DepthApiRawViewer : MonoBehaviour
{
    [Header("Depth API")]
    [Tooltip("The EnvironmentDepthManager present in the scene.")]
    [SerializeField]
    private EnvironmentDepthManager depthManager;

    [Header("Raw Display")]
    [Tooltip("GameObject (e.g. a quad) carrying the RawEnvironmentDepth material. Should start disabled in the scene.")]
    [SerializeField]
    private GameObject depthDisplayObject;

    private bool isDepthViewActive;

    private void Awake()
    {
        // Make sure we start deactivated regardless of how the scene was saved.
        isDepthViewActive = false;
        SetDepthViewActive(false);
    }

    private void Update()
    {
        bool aButtonPressedThisFrame = OVRInput.GetDown(OVRInput.RawButton.A);

        if (aButtonPressedThisFrame)
        {
            ToggleDepthView();
        }
    }

    private void ToggleDepthView()
    {
        bool newState = !isDepthViewActive;
        SetDepthViewActive(newState);
    }

    private void SetDepthViewActive(bool shouldBeActive)
    {
        isDepthViewActive = shouldBeActive;

        if (depthManager != null)
        {
            // Disabling the manager when not in use stops the system from
            // continuing to generate depth textures, which saves resources.
            depthManager.enabled = shouldBeActive;
        }
        else
        {
            Debug.LogWarning("[DepthApiRawViewer] No EnvironmentDepthManager assigned.");
        }

        if (depthDisplayObject != null)
        {
            depthDisplayObject.SetActive(shouldBeActive);
        }
        else
        {
            Debug.LogWarning("[DepthApiRawViewer] No depth display GameObject assigned.");
        }

        string stateLabel = shouldBeActive ? "ACTIVATED" : "DEACTIVATED";
        Debug.Log("[DepthApiRawViewer] Raw depth view " + stateLabel);
    }
}

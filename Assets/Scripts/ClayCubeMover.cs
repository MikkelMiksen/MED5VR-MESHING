using UnityEngine;

/// <summary>
/// Placement mode for ClayCube: pinch the cube to move/rotate it, pinch with both hands to scale it.
/// Sculpting is disabled while placing, so hands can't accidentally carve.
///
/// Setup: add this to any GameObject (e.g. the same one as ClayCube). The skeleton fields are
/// filled in from the ClayCube automatically if you leave them empty.
///
/// Placement mode:
///   - Cube is tinted BLUE; GREEN while one hand holds it; ORANGE while two hands are scaling it.
///   - Pinch thumb + index with the pinch point within a few cm of the cube, then move/rotate your
///     hand: the cube follows it.
///   - Pinch with BOTH hands on the cube and move them apart/together to scale it. Releasing either
///     pinch ends the scaling.
/// Sculpt mode: cube is tinted clay colour, hands carve as normal, the cube can't be moved.
///
/// Switch modes:
///   - Left hand: pinch thumb + MIDDLE finger and hold ~0.5 s
///   - Left controller: Y button
///   - Or call SetPlacementMode(bool) from your own UI
///
/// Pinch detection uses Meta's own pinch state (OVRHand) when available, which has proper
/// release behaviour. Fingertip distance is only a fallback if no OVRHand is found.
///
/// Debugging on the headset: adb logcat -s Unity   (look for lines starting with ClayCubeMover)
/// </summary>
public class ClayCubeMover : MonoBehaviour
{
    public ClayCube clay;
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    [Header("Grab")]
    [Tooltip("Pinch point must be within this distance (m) of the cube to grab it.")]
    public float grabDistance = 0.10f;
    [Tooltip("Fallback only (used when no OVRHand is found): fingertip distance to start / end a pinch.")]
    public float pinchOn = 0.020f;
    public float pinchOff = 0.035f;
    [Tooltip("Limits for the cube's uniform scale (transform scale, not meters).")]
    public float minScale = 0.25f;
    public float maxScale = 5f;

    [Header("Mode")]
    public bool placementMode = true;
    public float modeHoldTime = 0.5f;

    [Header("Feedback")]
    public Color placementColor = new Color(0.5f, 0.8f, 1f);
    public Color holdingColor = new Color(0.4f, 1f, 0.5f);
    public Color scalingColor = new Color(1f, 0.6f, 0.2f);
    public Color sculptColor = new Color(0.78f, 0.45f, 0.35f);
    public bool logEvents = true;

    class Hand
    {
        public string name;
        public OVRHand ovr;
        public bool valid, pinching, wasPinching, holding;
        public bool distPinching;            // fallback pinch state (distance based)
        public bool modePinching, modeReleased;
        public Vector3 pinchPoint;
        public Quaternion wristRot = Quaternion.identity;
        // cube pose expressed in this hand's frame, captured when the grab starts
        public Vector3 grabPos;
        public Quaternion grabRot = Quaternion.identity;
    }

    readonly Hand L = new Hand { name = "left" };
    readonly Hand R = new Hand { name = "right" };

    // two-hand scaling
    bool twoHand;
    float startDist, startScale;
    Vector3 startOffset;
    Quaternion startRot;

    // mode toggle gesture
    float modeTimer;
    bool modeFired;

    Renderer rend;
    MaterialPropertyBlock mpb;
    int tintState = -1;

    void Start()
    {
        if (clay == null) clay = GetComponent<ClayCube>();
        if (clay == null)
        {
            Debug.LogWarning("ClayCubeMover: assign the ClayCube.");
            enabled = false;
            return;
        }

        // Reuse the skeletons already assigned on the ClayCube.
        if (leftSkeleton == null) leftSkeleton = clay.leftSkeleton;
        if (rightSkeleton == null) rightSkeleton = clay.rightSkeleton;

        L.ovr = leftSkeleton != null ? leftSkeleton.GetComponentInParent<OVRHand>() : null;
        R.ovr = rightSkeleton != null ? rightSkeleton.GetComponentInParent<OVRHand>() : null;

        Debug.Log($"ClayCubeMover: left skeleton={(leftSkeleton != null)} (OVRHand={(L.ovr != null)}), " +
                  $"right skeleton={(rightSkeleton != null)} (OVRHand={(R.ovr != null)})");
        if (leftSkeleton == null || rightSkeleton == null)
            Debug.LogWarning("ClayCubeMover: a skeleton is not assigned, that hand will be ignored.");

        rend = clay.GetComponent<Renderer>();
        mpb = new MaterialPropertyBlock();
        SetPlacementMode(placementMode);
    }

    public void SetPlacementMode(bool on)
    {
        placementMode = on;
        clay.sculptingEnabled = !on;
        L.holding = R.holding = false;
        twoHand = false;
        tintState = -1;
        UpdateTint();
        if (logEvents) Debug.Log("ClayCubeMover: placement mode = " + on);
    }

    void Update()
    {
        ReadHand(rightSkeleton, R);
        ReadHand(leftSkeleton, L);

        UpdateModeToggle(Time.deltaTime);
        if (OVRInput.GetDown(OVRInput.Button.Four)) SetPlacementMode(!placementMode); // left controller Y

        if (!placementMode)
        {
            L.holding = R.holding = false;
            twoHand = false;
            UpdateTint();
            return;
        }

        UpdateGrab(L);
        UpdateGrab(R);
        ApplyMovement();
        UpdateTint();
    }

    // ------------------------------------------------------------------ hands

    void ReadHand(OVRSkeleton skel, Hand h)
    {
        h.valid = false;
        // Note: deliberately NOT requiring "high confidence" here. Confidence can drop while the
        // fingers pinch, which would make the grab let go.
        if (skel == null || !skel.IsInitialized || !skel.IsDataValid)
        {
            h.pinching = false;
            h.distPinching = false;
            h.modePinching = false;
            return;
        }

        Transform thumb = null, index = null, middle = null, wrist = null;
        var bones = skel.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            switch (bones[i].Id)
            {
                case OVRSkeleton.BoneId.Hand_ThumbTip:  thumb = bones[i].Transform; break;
                case OVRSkeleton.BoneId.Hand_IndexTip:  index = bones[i].Transform; break;
                case OVRSkeleton.BoneId.Hand_MiddleTip: middle = bones[i].Transform; break;
                case OVRSkeleton.BoneId.Hand_WristRoot: wrist = bones[i].Transform; break;
            }
        }
        if (thumb == null || index == null || wrist == null) return;

        h.valid = true;
        bool useMeta = h.ovr != null && h.ovr.IsTracked;

        // Index pinch (grab). Meta's pinch state is authoritative when available; fingertip
        // distance (with its own hysteresis) is only a fallback.
        if (useMeta)
        {
            h.pinching = h.ovr.GetFingerIsPinching(OVRHand.HandFinger.Index);
            h.distPinching = false;
        }
        else
        {
            float d = Vector3.Distance(thumb.position, index.position);
            h.distPinching = h.distPinching ? d < pinchOff : d < pinchOn;
            h.pinching = h.distPinching;
        }

        h.pinchPoint = (thumb.position + index.position) * 0.5f;
        h.wristRot = wrist.rotation;

        // Middle pinch (mode switch).
        if (useMeta)
        {
            bool metaMiddle = h.ovr.GetFingerIsPinching(OVRHand.HandFinger.Middle);
            h.modePinching = metaMiddle;
            h.modeReleased = !metaMiddle;
        }
        else
        {
            float md = middle != null ? Vector3.Distance(thumb.position, middle.position) : 1f;
            h.modePinching = md < pinchOn;
            h.modeReleased = md > pinchOff;
        }
    }

    void UpdateModeToggle(float dt)
    {
        if (!L.valid)
        {
            modeTimer = 0f;
            modeFired = false;
            return;
        }

        if (L.modePinching)
        {
            modeTimer += dt;
            if (!modeFired && modeTimer >= modeHoldTime)
            {
                modeFired = true;
                SetPlacementMode(!placementMode);
            }
        }
        else if (L.modeReleased)
        {
            modeTimer = 0f;
            modeFired = false;
        }
    }

    // ------------------------------------------------------------ move / scale

    void UpdateGrab(Hand h)
    {
        bool rising = h.pinching && !h.wasPinching;
        h.wasPinching = h.pinching;

        if (!h.valid)
        {
            h.holding = false;
            return;
        }

        float dist = DistanceToCube(h.pinchPoint);
        if (rising && logEvents)
            Debug.Log($"ClayCubeMover: {h.name} pinch, distance to cube = {dist:F3} m (grab within {grabDistance:F3} m)");

        if (!h.holding)
        {
            // Grab whenever a pinch is active near the cube (no need to start the pinch there).
            if (h.pinching && dist < grabDistance)
            {
                BeginHold(h);
                if (logEvents) Debug.Log($"ClayCubeMover: {h.name} hand grabbed the cube");
            }
        }
        else if (!h.pinching)
        {
            h.holding = false;
            if (logEvents) Debug.Log($"ClayCubeMover: {h.name} hand released the cube");
        }
    }

    void BeginHold(Hand h)
    {
        h.holding = true;
        Transform t = clay.transform;
        Quaternion inv = Quaternion.Inverse(h.wristRot);
        h.grabPos = inv * (t.position - h.pinchPoint);
        h.grabRot = inv * t.rotation;
    }

    void ApplyMovement()
    {
        Transform t = clay.transform;

        if (L.holding && R.holding)
        {
            // Two hands: scale around the midpoint, keep rotation.
            Vector3 mid = (L.pinchPoint + R.pinchPoint) * 0.5f;
            float dist = Vector3.Distance(L.pinchPoint, R.pinchPoint);

            if (!twoHand)
            {
                twoHand = true;
                startDist = Mathf.Max(dist, 0.01f);
                startScale = t.localScale.x;
                startOffset = t.position - mid;
                startRot = t.rotation;
                if (logEvents) Debug.Log($"ClayCubeMover: two-hand scaling started (scale {startScale:F2})");
            }

            float s = Mathf.Clamp(startScale * (dist / startDist), minScale, maxScale);
            float ratio = s / startScale;
            t.position = mid + startOffset * ratio;
            t.rotation = startRot;
            t.localScale = Vector3.one * s;
            return;
        }

        if (twoHand)
        {
            // One hand let go: scaling ends, re-anchor the remaining hand to the cube's current pose.
            twoHand = false;
            if (logEvents) Debug.Log($"ClayCubeMover: two-hand scaling ended (scale {t.localScale.x:F2})");
            if (L.holding) BeginHold(L);
            if (R.holding) BeginHold(R);
        }

        Hand h = L.holding ? L : (R.holding ? R : null);
        if (h == null) return;

        t.rotation = h.wristRot * h.grabRot;
        t.position = h.pinchPoint + h.wristRot * h.grabPos;
    }

    float DistanceToCube(Vector3 worldPoint)
    {
        Transform t = clay.transform;
        Vector3 p = t.InverseTransformPoint(worldPoint);
        float half = clay.size * 0.5f;
        Vector3 q = new Vector3(Mathf.Abs(p.x) - half, Mathf.Abs(p.y) - half, Mathf.Abs(p.z) - half);
        Vector3 outside = new Vector3(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f), Mathf.Max(q.z, 0f));
        return outside.magnitude * t.lossyScale.x;
    }

    // --------------------------------------------------------------- feedback

    void UpdateTint()
    {
        if (rend == null) return;

        // 0 = sculpt, 1 = placement idle, 2 = one hand holding, 3 = two-hand scaling
        int state;
        if (!placementMode) state = 0;
        else if (twoHand) state = 3;
        else if (L.holding || R.holding) state = 2;
        else state = 1;

        if (state == tintState) return;
        tintState = state;

        Color c = state == 0 ? sculptColor
                : state == 1 ? placementColor
                : state == 2 ? holdingColor
                : scalingColor;
        rend.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c); // URP Lit
        mpb.SetColor("_Color", c);     // Built-in
        rend.SetPropertyBlock(mpb);
    }
}
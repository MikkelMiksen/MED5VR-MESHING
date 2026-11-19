using UnityEngine;

/// Step 1 (hand tracking): user-placed oriented bounding box that defines the TSDF voxel grid.
///
/// One hand pinching (thumb + index): grab the box, it follows that hand's position and rotation.
/// Both hands pinching: the two pinch points become opposite corners of the box
///   (box orientation stays as it was, size and centre follow your hands).
/// Left hand middle-finger pinch: lock / unlock the box. Lock before scanning.
public class ScanBounds : MonoBehaviour
{
    [Header("Assign from OVRCameraRig (OVRHandPrefab on each hand anchor)")]
    public OVRHand leftHand;
    public OVRSkeleton leftSkeleton;
    public OVRHand rightHand;
    public OVRSkeleton rightSkeleton;

    [Header("Box")]
    public Vector3 size = new Vector3(0.4f, 0.4f, 0.4f);
    public float minSize = 0.1f;
    public float maxSize = 1.5f;

    [Header("Grid")]
    public float targetVoxelSize = 0.005f;   // 5 mm
    public int maxResolutionPerAxis = 256;

    public bool Locked { get; private set; }

    LineRenderer line;
    Matrix4x4 grabOffset;
    int prevMode;            // 0 none, 1 grab, 2 two-hand corners
    bool prevGrabWasRight;
    bool lockGestureWasDown;

    static readonly int[] PathOrder = { 0, 1, 2, 3, 0, 4, 5, 1, 5, 6, 2, 6, 7, 3, 7, 4 };

    void Awake()
    {
        line = gameObject.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = false;
        line.widthMultiplier = 0.004f;
        line.positionCount = PathOrder.Length;
        // Sprites/Default can be stripped from builds: add it to Always Included Shaders,
        // or swap in your own unlit material.
        line.material = new Material(Shader.Find("Sprites/Default"));
        UpdateVisual();
    }

    void Update()
    {
        // Lock toggle: left middle-finger pinch (rising edge only)
        bool lockGesture = leftHand != null && leftHand.IsTracked &&
                           leftHand.GetFingerIsPinching(OVRHand.HandFinger.Middle);
        if (lockGesture && !lockGestureWasDown)
        {
            Locked = !Locked;
            prevMode = 0;
            UpdateVisual();
        }
        lockGestureWasDown = lockGesture;
        if (Locked) return;

        bool l = TryGetPinch(leftHand, leftSkeleton, out Vector3 lPos, out Quaternion lRot);
        bool r = TryGetPinch(rightHand, rightSkeleton, out Vector3 rPos, out Quaternion rRot);

        int mode = (l && r) ? 2 : (l || r) ? 1 : 0;
        bool grabIsRight = r;   // if only one hand pinches, this says which

        if (mode == 1)
        {
            Vector3 p = grabIsRight ? rPos : lPos;
            Quaternion q = grabIsRight ? rRot : lRot;
            Matrix4x4 hand = Matrix4x4.TRS(p, q, Vector3.one);

            if (prevMode != 1 || prevGrabWasRight != grabIsRight)
                grabOffset = hand.inverse * Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

            Matrix4x4 m = hand * grabOffset;
            transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            prevGrabWasRight = grabIsRight;
        }
        else if (mode == 2)
        {
            Matrix4x4 boxToWorld = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Matrix4x4 worldToBox = boxToWorld.inverse;
            Vector3 a = worldToBox.MultiplyPoint3x4(lPos);
            Vector3 b = worldToBox.MultiplyPoint3x4(rPos);

            Vector3 d = new Vector3(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
            size = new Vector3(
                Mathf.Clamp(d.x, minSize, maxSize),
                Mathf.Clamp(d.y, minSize, maxSize),
                Mathf.Clamp(d.z, minSize, maxSize));

            transform.position = boxToWorld.MultiplyPoint3x4((a + b) * 0.5f);
        }

        prevMode = mode;
        UpdateVisual();
    }

    // Pinch point = midpoint of thumb tip and index tip. Rotation = the hand's root rotation.
    static bool TryGetPinch(OVRHand hand, OVRSkeleton skel, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = Quaternion.identity;
        if (hand == null || skel == null) return false;
        if (!hand.IsTracked || hand.HandConfidence != OVRHand.TrackingConfidence.High) return false;
        if (!skel.IsInitialized || !skel.IsDataValid) return false;
        if (!hand.GetFingerIsPinching(OVRHand.HandFinger.Index)) return false;

        Transform thumb = null, index = null;
        foreach (var bone in skel.Bones)
        {
            if (bone.Id == OVRSkeleton.BoneId.Hand_ThumbTip) thumb = bone.Transform;
            else if (bone.Id == OVRSkeleton.BoneId.Hand_IndexTip) index = bone.Transform;
        }
        if (thumb == null || index == null) return false;

        pos = (thumb.position + index.position) * 0.5f;
        rot = hand.transform.rotation;
        return true;
    }

    void UpdateVisual()
    {
        Vector3 h = size * 0.5f;
        Vector3[] c =
        {
            new Vector3(-h.x, -h.y, -h.z), new Vector3( h.x, -h.y, -h.z),
            new Vector3( h.x, -h.y,  h.z), new Vector3(-h.x, -h.y,  h.z),
            new Vector3(-h.x,  h.y, -h.z), new Vector3( h.x,  h.y, -h.z),
            new Vector3( h.x,  h.y,  h.z), new Vector3(-h.x,  h.y,  h.z),
        };
        for (int i = 0; i < PathOrder.Length; i++) line.SetPosition(i, c[PathOrder[i]]);
        Color col = Locked ? Color.green : Color.yellow;
        line.startColor = col;
        line.endColor = col;
    }

    /// Everything the fusion step needs. Voxel (0,0,0) centre sits at the box's min corner.
    public void GetGrid(out Vector3Int resolution, out float voxelSize, out Matrix4x4 voxelToWorld)
    {
        float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        voxelSize = Mathf.Max(targetVoxelSize, longest / maxResolutionPerAxis);
        resolution = new Vector3Int(
            Mathf.Max(1, Mathf.CeilToInt(size.x / voxelSize)),
            Mathf.Max(1, Mathf.CeilToInt(size.y / voxelSize)),
            Mathf.Max(1, Mathf.CeilToInt(size.z / voxelSize)));

        Vector3 gridSize = new Vector3(resolution.x, resolution.y, resolution.z) * voxelSize;
        Vector3 origin = -gridSize * 0.5f + Vector3.one * (voxelSize * 0.5f);

        Matrix4x4 boxToWorld = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        voxelToWorld = boxToWorld * Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one * voxelSize);
    }
}
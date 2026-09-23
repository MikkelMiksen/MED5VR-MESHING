using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKMeasurement : MonoBehaviour
{
    private MRUKAnchor anchor;
    private Bounds bounds;

    public void Initialize(
        MRUKAnchor anchor,
        Bounds bounds)
    {
        this.anchor = anchor;
        this.bounds = bounds;

        CreateLabel();
    }

    private void CreateLabel()
    {
        GameObject labelObject =
            new GameObject("MeasurementLabel");

        labelObject.transform.SetParent(
            transform,
            false
        );

        // Put label slightly above the object.
        labelObject.transform.localPosition =
            new Vector3(
                0,
                bounds.extents.y + 0.1f,
                0
            );

        TextMesh text =
            labelObject.AddComponent<TextMesh>();

        text.text = BuildMeasurementText();

        text.characterSize = 0.03f;
        text.fontSize = 32;
        text.anchor = TextAnchor.MiddleCenter;

        // Make it readable from both sides.
        text.alignment = TextAlignment.Center;

        // Billboard toward the camera.
        labelObject.AddComponent<Billboard>();
    }

    private string BuildMeasurementText()
    {
        Vector3 size = bounds.size;

        return
            anchor.Label + "\n" +
            $"W: {size.x:F2} m\n" +
            $"H: {size.y:F2} m\n" +
            $"D: {size.z:F2} m";
    }
}
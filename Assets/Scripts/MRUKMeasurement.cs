
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKMeasurement : MonoBehaviour
{
    private MRUKAnchor anchor;
    private Bounds bounds;
    private GameObject labelObject;

    public void Initialize(MRUKAnchor anchor, Bounds bounds)
    {
        this.anchor = anchor;
        this.bounds = bounds;

        CreateLabel();
        Hide();
    }

    private void CreateLabel()
    {
        labelObject = new GameObject("MeasurementLabel");
        labelObject.transform.SetParent(transform, false);

        // Position the label above the object's bounding box.
        labelObject.transform.localPosition = new Vector3(
            bounds.center.x,
            bounds.max.y + 0.15f,
            bounds.center.z
        );

        TextMesh text = labelObject.AddComponent<TextMesh>();

        text.text = BuildMeasurementText();
        text.characterSize = 0.01f;
        text.fontSize = 64;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = Color.white;

        labelObject.AddComponent<Billboard>();
    }

    private string BuildMeasurementText()
    {
        Vector3 size = bounds.size;

        return anchor.Label + "\n" +
               $"W: {size.x:F2} m\n" +
               $"H: {size.y:F2} m\n" +
               $"D: {size.z:F2} m";
    }

    public void Show()
    {
        if (labelObject != null)
            labelObject.SetActive(true);
    }

    public void Hide()
    {
        if (labelObject != null)
            labelObject.SetActive(false);
    }
}

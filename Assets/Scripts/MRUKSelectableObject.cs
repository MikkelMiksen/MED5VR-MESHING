
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKSelectableObject : MonoBehaviour
{
    public MRUKAnchor Anchor { get; private set; }

    private LineRenderer[] lines;
    private MRUKMeasurement measurement;
    private bool selected;

    private Color normalColor = Color.white;
    private Color selectedColor = Color.green;

    public void Initialize(MRUKAnchor anchor, Bounds bounds)
    {
        Anchor = anchor;

        measurement = gameObject.AddComponent<MRUKMeasurement>();
        measurement.Initialize(anchor, bounds);
    }

    public void SetupVisuals()
    {
        lines = GetComponentsInChildren<LineRenderer>();
    }

    public void Select()
    {
        if (selected)
            return;

        selected = true;
        SetColor(selectedColor);
        measurement?.Show();

        Debug.Log("Selected: " + Anchor.Label);
    }

    public void Deselect()
    {
        if (!selected)
            return;

        selected = false;
        SetColor(normalColor);
        measurement?.Hide();
    }

    private void SetColor(Color color)
    {
        if (lines == null)
            return;

        foreach (LineRenderer line in lines)
        {
            if (line == null)
                continue;

            line.startColor = color;
            line.endColor = color;
        }
    }

    public bool IsSelected()
    {
        return selected;
    }
}

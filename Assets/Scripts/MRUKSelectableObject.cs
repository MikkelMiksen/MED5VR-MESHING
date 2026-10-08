using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKSelectableObject : MonoBehaviour
{
    public MRUKAnchor Anchor { get; private set; }

    private LineRenderer[] lines;
    private bool selected = false;

    public void Initialize(MRUKAnchor anchor)
    {
        Anchor = anchor;

        Debug.Log(
            "Selectable MRUK object created: " +
            anchor.Label
        );
    }

    // Called after the bounding box has been created.
    public void SetupVisuals()
    {
        lines = GetComponentsInChildren<LineRenderer>();

        Debug.Log(
            "Found " + lines.Length +
            " line renderers for " + Anchor.Label
        );
    }

    public void Select()
    {
        if (selected)
            return;

        selected = true;

        SetColor(Color.green);

        Debug.Log(
            "SELECTED: " +
            Anchor.Label
        );
    }

    public void Deselect()
    {
        if (!selected)
            return;

        selected = false;

        SetColor(Color.white);
    }

    private void SetColor(Color color)
    {
        if (lines == null)
            return;

        foreach (LineRenderer line in lines)
        {
            if (line != null)
            {
                line.startColor = color;
                line.endColor = color;
            }
        }
    }

    public bool IsSelected()
    {
        return selected;
    }
}
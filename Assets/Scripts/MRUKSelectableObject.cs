using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKSelectableObject : MonoBehaviour
{
    public MRUKAnchor Anchor { get; private set; }

    private Renderer[] renderers;
    private Color originalColor;
    private bool selected = false;

    public void Initialize(MRUKAnchor anchor)
    {
        Anchor = anchor;

        renderers = GetComponentsInChildren<Renderer>();

        if (renderers.Length > 0)
        {
            originalColor = renderers[0].material.color;
        }
    }

    public void Select()
    {
        selected = true;

        foreach (Renderer renderer in renderers)
        {
            renderer.material.color = Color.green;
        }

        Debug.Log("Selected: " + Anchor.Label);
    }

    public void Deselect()
    {
        selected = false;

        foreach (Renderer renderer in renderers)
        {
            renderer.material.color = originalColor;
        }
    }

    public bool IsSelected()
    {
        return selected;
    }
}
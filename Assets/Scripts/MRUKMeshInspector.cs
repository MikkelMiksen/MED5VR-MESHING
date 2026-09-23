using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKVisualInspector : MonoBehaviour
{
    [Header("Visualization")]
    [SerializeField] private Material boundingBoxMaterial;
    [SerializeField] private float lineWidth = 0.01f;

    [Header("Settings")]
    [SerializeField] private bool showFurniture = true;
    [SerializeField] private bool showWalls = false;
    [SerializeField] private bool showFloor = false;
    [SerializeField] private bool showCeiling = false;

    private readonly List<GameObject> visualizedAnchors = new();

    private void Start()
    {
        if (MRUK.Instance == null)
        {
            Debug.LogError("MRUK instance not found.");
            return;
        }

        // Important for MRUK 205:
        // If the scene is already loaded, this callback can execute immediately.
        MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
    }

    private void OnSceneLoaded()
    {
        Debug.Log("MRUK scene loaded - creating visual inspector.");

        ClearVisualization();

        MRUKRoom room = MRUK.Instance.GetCurrentRoom();

        if (room == null)
        {
            Debug.LogError("No MRUK room found.");
            return;
        }

        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (!ShouldVisualize(anchor))
                continue;

            CreateAnchorVisualization(anchor);
        }
    }

    private bool ShouldVisualize(MRUKAnchor anchor)
    {
        if (!anchor.VolumeBounds.HasValue)
            return false;

        string label = anchor.Label.ToString();

        if (label == "WALL_FACE")
            return showWalls;

        if (label == "FLOOR")
            return showFloor;

        if (label == "CEILING")
            return showCeiling;

        return showFurniture;
    }

    private void CreateAnchorVisualization(MRUKAnchor anchor)
    {
        Bounds bounds = anchor.VolumeBounds.Value;

        GameObject visual = new GameObject(
            "Inspector_" + anchor.Label
        );

        visual.transform.SetParent(anchor.transform, false);

        // Create a wireframe box.
        LineRenderer[] lines = CreateBoundingBox(
            visual.transform,
            bounds
        );

        foreach (LineRenderer line in lines)
        {
            line.startWidth = lineWidth;
            line.endWidth = lineWidth;

            if (boundingBoxMaterial != null)
                line.material = boundingBoxMaterial;
        }

        visualizedAnchors.Add(visual);
    }

    private LineRenderer[] CreateBoundingBox(
        Transform parent,
        Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, min.y, max.z),

            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z)
        };

        int[][] edges =
        {
            new[] { 0, 1 },
            new[] { 1, 2 },
            new[] { 2, 3 },
            new[] { 3, 0 },

            new[] { 4, 5 },
            new[] { 5, 6 },
            new[] { 6, 7 },
            new[] { 7, 4 },

            new[] { 0, 4 },
            new[] { 1, 5 },
            new[] { 2, 6 },
            new[] { 3, 7 }
        };

        LineRenderer[] renderers =
            new LineRenderer[edges.Length];

        for (int i = 0; i < edges.Length; i++)
        {
            GameObject edge =
                new GameObject("Edge_" + i);

            edge.transform.SetParent(parent, false);

            LineRenderer line =
                edge.AddComponent<LineRenderer>();

            line.useWorldSpace = false;
            line.positionCount = 2;

            line.SetPosition(
                0,
                corners[edges[i][0]]
            );

            line.SetPosition(
                1,
                corners[edges[i][1]]
            );

            renderers[i] = line;
        }

        return renderers;
    }

    private void ClearVisualization()
    {
        foreach (GameObject obj in visualizedAnchors)
        {
            if (obj != null)
                Destroy(obj);
        }

        visualizedAnchors.Clear();
    }
}
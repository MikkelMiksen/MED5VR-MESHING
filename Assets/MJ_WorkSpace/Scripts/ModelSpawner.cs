using GLTFast;
using UnityEngine;

public class ModelSpawner : MonoBehaviour
{
    [SerializeField] private Vector3 offset = new Vector3(0f, 0.15f, 0f);

    // Set this from your A-press code before starting the request
    public Bounds TargetBounds;

    private GameObject currentInstance;
    private GltfImport currentImport;

    private void OnEnable()
    {
        RoutineSender.ModelReady += OnModelReady;
    }

    private void OnDisable()
    {
        RoutineSender.ModelReady -= OnModelReady;
    }

    private async void OnModelReady(byte[] glbBytes)
    {
        // Step 1: remove the previous model and free its memory
        if (currentInstance != null)
        {
            Destroy(currentInstance);
        }
        if (currentImport != null)
        {
            currentImport.Dispose();
        }

        // Step 2: parse the GLB and spawn it
        GltfImport gltf = new GltfImport();
        bool loaded = await gltf.LoadGltfBinary(glbBytes);
        if (!loaded)
        {
            Debug.LogError("[ModelSpawner] Failed to parse GLB");
            gltf.Dispose();
            return;
        }

        GameObject root = new GameObject("GeneratedModel");
        await gltf.InstantiateMainSceneAsync(root.transform);

        currentImport = gltf;
        currentInstance = root;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

        // Step 3: scale the model to the height of the target box
        Bounds modelBounds = GetBounds(renderers);
        if (modelBounds.size.y > 0.0001f && TargetBounds.size.y > 0.0001f)
        {
            root.transform.localScale = Vector3.one * (TargetBounds.size.y / modelBounds.size.y);
        }

        // Step 4: move the model's center to the box center plus the offset
        modelBounds = GetBounds(renderers);
        root.transform.position += (TargetBounds.center + offset) - modelBounds.center;
    }

    private Bounds GetBounds(Renderer[] renderers)
    {
        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            combined.Encapsulate(renderers[i].bounds);
        }
        return combined;
    }
}
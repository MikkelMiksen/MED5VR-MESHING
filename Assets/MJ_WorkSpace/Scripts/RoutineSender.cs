using System.Collections;
using UnityEngine;
using System.IO;
using UnityEngine.Networking;

public class RoutineSender : MonoBehaviour
{
    [SerializeField] private Texture2D image;

    [Header("Server")]
    [SerializeField] private string serverUrl = "http://172.20.10.3:8000";
    [SerializeField] private int mcResolution = 128;
    [SerializeField] private bool removeBackground = true;
    [SerializeField] private int timeoutSeconds = 120;

    [Header("Output")]
    [SerializeField] private string outputFileName = "bottle_fixed.glb";
    
    public static event System.Action<byte[]> ModelReady;

    private IEnumerator Start()
    {
        Debug.Log("[RoutineSender] Starting connectivity check");
        yield return new WaitForSeconds(1f);
        yield return ServerDebugger();
    }

    public IEnumerator ServerDebugger()
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Debug.LogError("[RoutineSender] serverUrl is empty");
            yield break;
        }

        string url = serverUrl.TrimEnd('/') + "/health";
        Debug.Log("[RoutineSender] Sending GET " + url);

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = timeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[RoutineSender] Health check failed: " + request.error);
                yield break;
            }

            Debug.Log("[RoutineSender] Health check succeeded: HTTP " + request.responseCode);
        }
    }

    public IEnumerator SendImage(Texture2D file)
    {
        if (file == null)
        {
            Debug.LogError("[RoutineSender] Cannot send a null image");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Debug.LogError("[RoutineSender] serverUrl is empty");
            yield break;
        }

        byte[] pngBytes = file.EncodeToPNG();

        WWWForm form = new WWWForm();
        form.AddBinaryData("file", pngBytes, "bottle_fixed.png", "image/png");

        string url = serverUrl.TrimEnd('/') + "/reconstruct"
            + "?mc_resolution=" + mcResolution
            + "&remove_bg=" + (removeBackground ? "true" : "false");

        Debug.Log("[RoutineSender] Sending POST " + url + " (" + pngBytes.Length + " bytes)");

        using (UnityWebRequest request = UnityWebRequest.Post(url, form))
        {
            request.timeout = timeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[RoutineSender] Reconstruction failed: " + request.error
                    + " HTTP " + request.responseCode);
                yield break;
            }

            Debug.Log("[RoutineSender] Reconstruction succeeded: HTTP " + request.responseCode);
            byte[] glbBytes = request.downloadHandler.data;
            SaveGlb(glbBytes);
            ModelReady?.Invoke(glbBytes);
        }
    }

    void SaveGlb(byte[] glbBytes)
    {
        if (glbBytes == null || glbBytes.Length < 12)
        {
            Debug.LogError("Response too small to be a GLB: " + (glbBytes == null ? 0 : glbBytes.Length) + " bytes");
            return;
        }

        string magic = System.Text.Encoding.ASCII.GetString(glbBytes, 0, 4);
        Debug.Log("First 4 bytes: '" + magic + "', total " + glbBytes.Length + " bytes");

        if (magic != "glTF")
        {
            int previewLength = Mathf.Min(glbBytes.Length, 300);
            Debug.LogError("Not a GLB. Body starts with: " +
                System.Text.Encoding.UTF8.GetString(glbBytes, 0, previewLength));
            return;
        }

#if UNITY_EDITOR
        string folder = Path.Combine(Application.dataPath, "GeneratedModels");
#else
        string folder = Path.Combine(Application.persistentDataPath, "GeneratedModels");
#endif
        Directory.CreateDirectory(folder);

        string fullPath = Path.Combine(folder, outputFileName);
        File.WriteAllBytes(fullPath, glbBytes);
        Debug.Log("Saved GLB (" + glbBytes.Length / 1024 + " KB) to " + fullPath);

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }
}

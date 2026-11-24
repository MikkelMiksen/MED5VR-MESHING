using System.Collections;
using UnityEngine;
using System.IO;
using UnityEngine.Networking;


public class RoutineSender : MonoBehaviour
{
    [SerializeField] private Texture2D image;
    
    [Header("Server")]
    [SerializeField] private string serverUrl = "http://172.20.10.3:8000/reconstruct";  // laptop IP, not localhost on Quest
    [SerializeField] private int mcResolution = 128;
    [SerializeField] private bool removeBackground = true;
    [SerializeField] private int timeoutSeconds = 120;

    [Header("Output")]
    [SerializeField] private string outputFileName = "bottle_fixed.glb";
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    /*
    IEnumerator Start()
    {
        //image = Resources.Load<Texture2D>("Assets/bottle_fixed.png");
        yield return new WaitForSeconds(1f);
        StartCoroutine(SendImage(image));
    }
    */

    public IEnumerator SendImage(Texture2D file)
    {
       byte[] pngBytes = file.EncodeToPNG();

       WWWForm form = new();
       form.AddBinaryData("file",pngBytes, "bottle_fixed", "image/png");
       string url = serverUrl + "/reconstruct" + "?mc_resolution=" + mcResolution + "&remove_bg=" +
                    (removeBackground ? "true" : "false");

       using (UnityWebRequest request = UnityWebRequest.Post(url, form))
       {
           request.timeout = timeoutSeconds;
           yield return request.SendWebRequest();

           if (request.result != UnityWebRequest.Result.Success)
           {
               Debug.LogError("Reconstruction failed: " + request.error);
               yield break;
           }

           byte[] glbBytes = request.downloadHandler.data;
           SaveGlb(glbBytes);
       }
    }

    void SaveGlb(byte[] glbBytes)
    {
        // Step 0: check that the response really is a GLB (magic bytes "glTF")
        if (glbBytes == null || glbBytes.Length < 12)
        {
            Debug.LogError("Response too small to be a GLB: " + (glbBytes == null ? 0 : glbBytes.Length) + " bytes");
            return;
        }

        string magic = System.Text.Encoding.ASCII.GetString(glbBytes, 0, 4);
        Debug.Log("First 4 bytes: '" + magic + "', total " + glbBytes.Length + " bytes");

        if (magic != "glTF")
        {
            // Not a GLB, so print the start of the body to see what the server actually sent
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

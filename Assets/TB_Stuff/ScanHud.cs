using System.Text;
using UnityEngine;

/// In-headset status readout that floats above the scan box and faces you.
/// It only reads numbers the scan already collects, and refreshes a few times per second.
/// Box outline colour: yellow = unlocked, orange = waiting for depth, green = scanning, cyan = paused.
public class ScanHud : MonoBehaviour
{
    [Header("References")]
    public ScanBounds bounds;
    public TSDFVolume volume;
    public SurfaceMesher mesher;
    public DepthSource depth;
    public Transform head;            // leave empty to use Camera.main (the rig's centre eye)

    [Header("Look")]
    public float heightAboveBox = 0.08f;
    public float characterSize = 0.003f;   // raise or lower if the text is too small or too big
    public int fontSize = 64;
    public float refreshRate = 4f;

    TextMesh textMesh;
    Transform hud;
    float nextRefresh;
    int lastState = -1;
    readonly StringBuilder sb = new StringBuilder(300);

    void Awake()
    {
        var go = new GameObject("ScanHud");
        hud = go.transform;
        textMesh = go.AddComponent<TextMesh>();
        textMesh.anchor = TextAnchor.LowerCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.characterSize = characterSize;
        textMesh.fontSize = fontSize;
        textMesh.color = Color.white;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font == null)
        {
            Debug.LogWarning("ScanHud: no built-in font found, the readout will not render.");
            return;
        }
        textMesh.font = font;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
    }

    void OnDestroy()
    {
        if (hud != null) Destroy(hud.gameObject);
    }

    void LateUpdate()
    {
        if (bounds == null || textMesh == null) return;
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (head == null) return;

        float r = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * 0.5f;
        Vector3 pos = bounds.transform.position + Vector3.up * (r + heightAboveBox);
        hud.position = pos;

        Vector3 away = pos - head.position;
        if (away.sqrMagnitude > 1e-4f) hud.rotation = Quaternion.LookRotation(away, Vector3.up);

        if (Time.time < nextRefresh) return;
        nextRefresh = Time.time + 1f / refreshRate;
        Refresh();
    }

    void Refresh()
    {
        sb.Clear();

        if (!bounds.Locked)
        {
            lastState = -1;
            sb.Append("Size the box: pinch with both hands\nLock: left middle finger + thumb");
            textMesh.text = sb.ToString();
            return;
        }

        int state;
        string label;
        Color col;
        if (volume == null || !volume.Active) { state = 0; label = "STARTING"; col = Color.yellow; }
        else if (volume.Paused)               { state = 3; label = "PAUSED";   col = Color.cyan; }
        else if (depth != null && !depth.IsReady) { state = 1; label = "WAITING FOR DEPTH"; col = new Color(1f, 0.55f, 0f); }
        else                                  { state = 2; label = "SCANNING"; col = Color.green; }

        if (state != lastState)
        {
            lastState = state;
            bounds.SetLockedColor(col);
        }

        sb.Append(label);
        if (volume != null) sb.Append("   frames ").Append(volume.Version);
        sb.Append('\n');

        if (volume != null && volume.LastTotalVoxels > 0)
        {
            float pct = 100f * volume.LastObserved / volume.LastTotalVoxels;
            sb.Append("observed ").Append(pct.ToString("F0")).Append("%   surface ")
              .Append(volume.LastNearSurface).Append('\n');
        }
        else sb.Append("observed -   surface -\n");

        if (mesher != null) sb.Append("mesh ").Append(mesher.LastTriangleCount).Append(" tris\n");

        if (volume != null)
        {
            uint[] d = volume.LastDebug;
            float total = 0f;
            for (int i = 9; i <= 16; i++) total += d[i];
            if (total > 0f)
            {
                float near = 100f * (d[10] + d[11] + d[12] + d[13]) / total;   // under 1.1 m
                float far = 100f * (d[9] + d[15] + d[16]) / total;             // beyond 1.5 m or invalid
                sb.Append("depth <1.1m ").Append(near.ToString("F0")).Append("%  >1.5m ")
                  .Append(far.ToString("F0")).Append("%\n");
                sb.Append("hits ").Append(d[17]).Append(" | X ").Append(d[18])
                  .Append(" | Y ").Append(d[19]).Append(" | XY ").Append(d[20]);
            }
        }

        if (volume != null && volume.HasProbe)
        {
            float[] p = volume.LastProbe;
            sb.Append("\nahead 1m  L: w ").Append(p[0].ToString("F2"))
              .Append(" uv ").Append(p[1].ToString("F2")).Append(',').Append(p[2].ToString("F2"))
              .Append(" depth ").Append(p[3] < 0f ? "-" : p[3].ToString("F2")).Append('m');
            sb.Append("\nahead 1m  R: w ").Append(p[4].ToString("F2"))
              .Append(" uv ").Append(p[5].ToString("F2")).Append(',').Append(p[6].ToString("F2"))
              .Append(" depth ").Append(p[7] < 0f ? "-" : p[7].ToString("F2")).Append('m');
        }

        textMesh.text = sb.ToString();
    }
}
using UnityEngine;
using TMPro;

public class FpsCounter : MonoBehaviour {
    [SerializeField] private TextMeshProUGUI label;

    [Tooltip("How fast the displayed value smooths toward the latest reading. " +
             "Lower = smoother but laggier; higher = jumpier but more responsive.")]
    [Range(0.01f, 1f)][SerializeField] private float smoothing = 0.1f;

    [Tooltip("Update the label every N frames. 1 = every frame; higher reduces UI churn.")]
    [SerializeField] private int updateEveryFrames = 5;

    private float smoothedDt;
    private int frameCounter;

    void Awake() {
        if (label == null) label = GetComponent<TextMeshProUGUI>();
        smoothedDt = Time.unscaledDeltaTime;
    }

    void Update() {
        // Exponential smoothing on the frame time.
        smoothedDt += (Time.unscaledDeltaTime - smoothedDt) * smoothing;

        if (++frameCounter < updateEveryFrames) return;
        frameCounter = 0;

        if (label == null) return;
        float fps = 1f / smoothedDt;
        float ms = smoothedDt * 1000f;
        label.text = $"{fps:F0} fps  ({ms:F1} ms)";
    }
}

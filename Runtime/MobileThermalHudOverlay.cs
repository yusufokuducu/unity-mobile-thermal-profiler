using UnityEngine;

namespace YusufOkuducu.MobileThermalProfiler
{
    /// <summary>
    /// Drop-in runtime HUD overlay component for on-device mobile testing.
    /// Displays live FPS, 1% / 0.1% Lows, Frame Jitter, CPU/GPU/Thermal bottleneck state, and memory.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Performance/Mobile Thermal & Bandwidth HUD")]
    public sealed class MobileThermalHudOverlay : MonoBehaviour
    {
        [Header("Sampling Configuration")]
        [SerializeField] private int sampleWindowSize = 600;
        [SerializeField] private float targetFrameRate = 60f;
        [SerializeField] private float summaryRefreshIntervalSec = 0.5f;

        [Header("HUD Appearance")]
        [SerializeField] private bool showOverlay = true;
        [SerializeField] private Rect hudRect = new Rect(16f, 16f, 360f, 165f);

        private MobileThermalTelemetry _telemetry;
        private TelemetrySummary _cachedSummary;
        private FrameTelemetrySample _latestSample;
        private float _nextRefreshTime;
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;

        public MobileThermalTelemetry Telemetry => _telemetry;
        public TelemetrySummary LatestSummary => _cachedSummary;

        private void Awake()
        {
            _telemetry = new MobileThermalTelemetry(sampleWindowSize);
        }

        private void Update()
        {
            _latestSample = _telemetry.CaptureFrame(targetFrameRate);

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _cachedSummary = _telemetry.ComputeSummary(targetFrameRate);
                _nextRefreshTime = Time.unscaledTime + Mathf.Max(0.1f, summaryRefreshIntervalSec);
            }
        }

        private void OnGUI()
        {
            if (!showOverlay) return;

            EnsureStyles();

            GUI.Box(hudRect, GUIContent.none, _boxStyle);
            GUILayout.BeginArea(new Rect(hudRect.x + 10f, hudRect.y + 8f, hudRect.width - 20f, hudRect.height - 16f));

            GUILayout.Label($"<b>MOBILE THERMAL & PACING HUD</b> [{_cachedSummary.dominantBottleneck}]", _labelStyle);
            GUILayout.Label(
                $"FPS: <b>{_cachedSummary.avgFps:F1}</b> | 1% Low: <b>{_cachedSummary.onePercentLowFps:F1}</b> | 0.1% Low: <b>{_cachedSummary.pointOnePercentLowFps:F1}</b>",
                _labelStyle);
            GUILayout.Label(
                $"Frame: {_cachedSummary.avgFrameMs:F2} ms (P99: {_cachedSummary.p99FrameMs:F2} ms | Jitter σ: {_cachedSummary.frameTimeJitterStdDevMs:F2} ms)",
                _labelStyle);
            GUILayout.Label(
                $"CPU Main: {_latestSample.cpuMainThreadMs:F2} ms | Render: {_latestSample.cpuRenderThreadMs:F2} ms | GPU: {_latestSample.gpuFrameTimeMs:F2} ms",
                _labelStyle);
            GUILayout.Label(
                $"Thermal Load: {(_latestSample.thermalHeadroomNormalized * 100f):F0}% | VSync Misses: {_cachedSummary.vsyncMissFrames}/{_cachedSummary.sampleCount}",
                _labelStyle);
            GUILayout.Label(
                $"Alloc: {(_latestSample.allocatedMemoryBytes / (1024f * 1024f)):F1} MB | Mono: {(_latestSample.monoHeapBytes / (1024f * 1024f)):F1} MB",
                _labelStyle);

            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_boxStyle != null) return;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 12
            };
            _labelStyle.normal.textColor = Color.white;
        }
    }
}

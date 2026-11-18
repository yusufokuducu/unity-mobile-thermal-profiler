using System.IO;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.MobileThermalProfiler.Editor
{
    public sealed class ThermalBandwidthProfilerWindow : EditorWindow
    {
        private TbdrAuditReport _lastAuditReport;
        private Vector2 _scrollPos;
        private float _simulatedTargetFps = 60f;

        [MenuItem("Window/Analysis/Mobile Thermal & TBDR Bandwidth Profiler")]
        public static void ShowWindow()
        {
            var win = GetWindow<ThermalBandwidthProfilerWindow>("Mobile TBDR & Thermal");
            win.minSize = new Vector2(620f, 440f);
            win.Show();
        }

        private void OnEnable()
        {
            RunTbdrAudit();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Mobile Thermal, Frame-Pacing & TBDR Bandwidth Auditor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Audits active RenderTextures for Tile-Based Deferred Rendering (Mali / Adreno / Apple GPU) DRAM bandwidth hazards and inspects live MobileThermalHudOverlay telemetry.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Audit Active RenderTextures (TBDR Bandwidth)", GUILayout.Height(28f)))
            {
                RunTbdrAudit();
            }

            if (GUILayout.Button("Add Runtime HUD Overlay to Scene", GUILayout.Height(28f)))
            {
                AddHudToScene();
            }

            if (GUILayout.Button("Export Live Telemetry CSV", GUILayout.Height(28f)))
            {
                ExportLiveCsv();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8f);
            DrawLivePlayModeSection();
            EditorGUILayout.Space(8f);
            DrawTbdrAuditSection();
        }

        private void DrawLivePlayModeSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Live Play Mode Telemetry", EditorStyles.boldLabel);

            var activeHud = FindFirstObjectByType<MobileThermalHudOverlay>();
            if (activeHud == null || activeHud.Telemetry == null || activeHud.Telemetry.Count == 0)
            {
                EditorGUILayout.LabelField("Enter Play Mode with a MobileThermalHudOverlay in the scene to view live 1% / 0.1% low frame-pacing metrics.");
                EditorGUILayout.EndVertical();
                return;
            }

            _simulatedTargetFps = EditorGUILayout.Slider("Target Frame Rate", _simulatedTargetFps, 30f, 120f);
            TelemetrySummary sum = activeHud.Telemetry.ComputeSummary(_simulatedTargetFps);

            EditorGUILayout.LabelField($"Dominant Bottleneck: {sum.dominantBottleneck}");
            EditorGUILayout.LabelField($"Avg FPS: {sum.avgFps:F1} | 1% Low: {sum.onePercentLowFps:F1} FPS | 0.1% Low: {sum.pointOnePercentLowFps:F1} FPS");
            EditorGUILayout.LabelField($"Frame Time: {sum.avgFrameMs:F2} ms | P99: {sum.p99FrameMs:F2} ms | P99.9: {sum.p99_9FrameMs:F2} ms | Jitter σ: {sum.frameTimeJitterStdDevMs:F2} ms");
            EditorGUILayout.LabelField($"Peak Thermal Drift: {(sum.peakThermalLoad * 100f):F0}% | VSync Miss Frames: {sum.vsyncMissFrames}/{sum.sampleCount}");
            EditorGUILayout.EndVertical();

            if (Application.isPlaying)
                Repaint();
        }

        private void DrawTbdrAuditSection()
        {
            if (_lastAuditReport == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("TBDR Memory Bandwidth Audit Report", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"RenderTextures Audited: {_lastAuditReport.totalRenderTexturesAudited} | " +
                $"Est. DRAM Traffic @ 60 FPS: {_lastAuditReport.estimatedTotalBandwidthGbpsAt60Fps:F2} GB/s | " +
                $"@ 120 FPS: {_lastAuditReport.estimatedTotalBandwidthGbpsAt120Fps:F2} GB/s");

            if (_lastAuditReport.estimatedTotalBandwidthGbpsAt60Fps > 4.5f)
            {
                EditorGUILayout.HelpBox(
                    "WARNING: Estimated RenderTexture DRAM write/read traffic exceeds 4.5 GB/s at 60 FPS. On mid-range mobile SoCs, external memory bandwidth above ~4-5 GB/s causes severe battery drain and thermal throttling within 8-12 minutes.",
                    MessageType.Warning);
            }

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (var f in _lastAuditReport.findings)
            {
                EditorGUILayout.BeginVertical( GUI.skin.box);
                EditorGUILayout.LabelField(
                    $"{f.textureName} ({f.width}x{f.height}, MSAA {f.antiAliasing}x, {f.colorFormat})",
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"Est. Tile Flush Traffic: {f.estimatedTileStoreMegabytesPerFrame:F2} MB/frame ({(f.estimatedTileStoreMegabytesPerFrame * 60f / 1024f):F2} GB/s @ 60 FPS)");
                EditorGUILayout.LabelField($"Recommendation: {f.recommendation}", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void RunTbdrAudit()
        {
            _lastAuditReport = TbdrBandwidthAuditor.AuditActiveRenderTextures();
        }

        private static void AddHudToScene()
        {
            var existing = FindFirstObjectByType<MobileThermalHudOverlay>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            var go = new GameObject("MobileThermalProfilerHUD");
            Undo.RegisterCreatedObjectUndo(go, "Create MobileThermalProfilerHUD");
            go.AddComponent<MobileThermalHudOverlay>();
            Selection.activeGameObject = go;
        }

        private static void ExportLiveCsv()
        {
            var activeHud = FindFirstObjectByType<MobileThermalHudOverlay>();
            if (activeHud == null || activeHud.Telemetry == null || activeHud.Telemetry.Count == 0)
            {
                EditorUtility.DisplayDialog("No Telemetry Data", "No active MobileThermalHudOverlay samples found.", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanel("Export Mobile Telemetry CSV", "", "mobile_thermal_telemetry.csv", "csv");
            if (!string.IsNullOrEmpty(path))
            {
                activeHud.Telemetry.SaveCsvToFile(path);
                EditorUtility.RevealInFinder(path);
            }
        }
    }
}

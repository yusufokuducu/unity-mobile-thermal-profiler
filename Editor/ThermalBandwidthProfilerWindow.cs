using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace YusufOkuducu.MobileThermalProfiler.Editor
{
    public sealed class ThermalBandwidthProfilerWindow : EditorWindow
    {
        private TbdrAuditReport _lastAuditReport;
        private UrpPipelineBandwidthEstimate _pipelineEstimate;
        private Vector2 _scrollPos;
        private float _simulatedTargetFps = 60f;

        // URP Pipeline Simulator Settings
        private int _simWidth = 2400;
        private int _simHeight = 1080;
        private int _simMsaa = 4;
        private GraphicsFormat _simColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
        private bool _simDepthTex = false;
        private bool _simOpaqueTex = false;
        private bool _simBloom = true;
        private int _simShadowRes = 2048;
        private MobileSoCTier _simSocTier = MobileSoCTier.MidRangeMainstream;

        [MenuItem("Window/Analysis/Mobile Thermal & TBDR Bandwidth Profiler")]
        public static void ShowWindow()
        {
            var win = GetWindow<ThermalBandwidthProfilerWindow>("Mobile TBDR & Thermal");
            win.minSize = new Vector2(660f, 520f);
            win.Show();
        }

        private void OnEnable()
        {
            RunTbdrAudit();
            RecalculatePipelineSimulation();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Mobile Thermal, Frame-Pacing & TBDR Bandwidth Auditor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Audits active RenderTextures & URP camera pipelines for Tile-Based Deferred Rendering (Mali / Adreno / Apple GPU) DRAM bandwidth hazards, estimates mW memory power draw, and inspects live MobileThermalHudOverlay telemetry.",
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

            if (GUILayout.Button("Export Live Telemetry CSV & Markdown", GUILayout.Height(28f)))
            {
                ExportLiveReports();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8f);
            DrawLivePlayModeSection();
            EditorGUILayout.Space(8f);
            DrawUrpPipelineSimulatorSection();
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
                EditorGUILayout.LabelField("Enter Play Mode with a MobileThermalHudOverlay in the scene to view live 1% / 0.1% low frame-pacing and OLS thermal slope metrics.");
                EditorGUILayout.EndVertical();
                return;
            }

            _simulatedTargetFps = EditorGUILayout.Slider("Target Frame Rate", _simulatedTargetFps, 30f, 120f);
            TelemetrySummary sum = activeHud.Telemetry.ComputeSummary(_simulatedTargetFps);

            EditorGUILayout.LabelField($"Dominant Bottleneck: {sum.dominantBottleneck} | Thermal State: {sum.thermalState} (Slope: {sum.thermalDriftSlopeMsPerSec:+0.000;-0.000;0.000} ms/s)");
            EditorGUILayout.LabelField($"Avg FPS: {sum.avgFps:F1} | 1% Low: {sum.onePercentLowFps:F1} FPS | 0.1% Low: {sum.pointOnePercentLowFps:F1} FPS");
            EditorGUILayout.LabelField($"Frame Time: {sum.avgFrameMs:F2} ms (Median: {sum.p50FrameMs:F2} ms | P95: {sum.p95FrameMs:F2} ms | P99: {sum.p99FrameMs:F2} ms | Jitter σ: {sum.frameTimeJitterStdDevMs:F2} ms)");
            EditorGUILayout.LabelField($"Peak Thermal Load: {(sum.peakThermalLoad * 100f):F0}% | VSync Misses: {sum.vsyncMissFrames}/{sum.sampleCount} (Max Streak: {sum.longestVSyncDropStreak}) | GC Spike Frames: {sum.gcAllocationSpikeFrames}");
            EditorGUILayout.EndVertical();

            if (Application.isPlaying)
                Repaint();
        }

        private void DrawUrpPipelineSimulatorSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("URP Mobile Camera Pipeline Bandwidth & Power Simulator", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _simWidth = EditorGUILayout.IntField("Width", _simWidth);
            _simHeight = EditorGUILayout.IntField("Height", _simHeight);
            _simMsaa = EditorGUILayout.IntField("MSAA Samples", _simMsaa);
            _simShadowRes = EditorGUILayout.IntField("Shadow Atlas", _simShadowRes);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _simSocTier = (MobileSoCTier)EditorGUILayout.EnumPopup("Target SoC Tier", _simSocTier);
            _simColorFormat = (GraphicsFormat)EditorGUILayout.EnumPopup("Color Format", _simColorFormat);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _simDepthTex = EditorGUILayout.ToggleLeft("Require _CameraDepthTexture", _simDepthTex);
            _simOpaqueTex = EditorGUILayout.ToggleLeft("Require _CameraOpaqueTexture", _simOpaqueTex);
            _simBloom = EditorGUILayout.ToggleLeft("URP Bloom Enabled", _simBloom);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Recalculate URP Pipeline Bandwidth & DRAM Power", GUILayout.Height(24f)))
            {
                RecalculatePipelineSimulation();
            }

            if (_pipelineEstimate.passBreakdown != null)
            {
                EditorGUILayout.LabelField(
                    $"Total Frame Traffic: {_pipelineEstimate.totalMegabytesPerFrame:F2} MB/frame | " +
                    $"@ 60 FPS: {_pipelineEstimate.bandwidthGbpsAt60Fps:F2} GB/s (~{_pipelineEstimate.estimatedDramPowerMilliwattsAt60Fps:F0} mW LPDDR Power) | " +
                    $"@ 120 FPS: {_pipelineEstimate.bandwidthGbpsAt120Fps:F2} GB/s",
                    EditorStyles.boldLabel);

                for (int i = 0; i < _pipelineEstimate.passBreakdown.Count; i++)
                {
                    EditorGUILayout.LabelField("• " + _pipelineEstimate.passBreakdown[i], EditorStyles.wordWrappedMiniLabel);
                }

                if (_pipelineEstimate.optimizationAdvice != null)
                {
                    for (int i = 0; i < _pipelineEstimate.optimizationAdvice.Count; i++)
                    {
                        EditorGUILayout.LabelField("→ " + _pipelineEstimate.optimizationAdvice[i], EditorStyles.wordWrappedMiniLabel);
                    }
                }

                if (_pipelineEstimate.exceedsTierThermalEnvelope)
                {
                    EditorGUILayout.HelpBox(
                        $"THERMAL ENVELOPE EXCEEDED: {_pipelineEstimate.bandwidthGbpsAt60Fps:F2} GB/s exceeds the sustained budget ({TbdrBandwidthAuditor.GetSustainedBandwidthBudgetGbps(_simSocTier):F1} GB/s) for {_simSocTier}.",
                        MessageType.Warning);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawTbdrAuditSection()
        {
            if (_lastAuditReport == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("TBDR Memory Bandwidth Audit Report (Allocated RenderTextures)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"RenderTextures Audited: {_lastAuditReport.totalRenderTexturesAudited} | " +
                $"Est. DRAM Traffic @ 60 FPS: {_lastAuditReport.estimatedTotalBandwidthGbpsAt60Fps:F2} GB/s (~{_lastAuditReport.estimatedDramPowerMilliwattsAt60Fps:F0} mW) | " +
                $"@ 120 FPS: {_lastAuditReport.estimatedTotalBandwidthGbpsAt120Fps:F2} GB/s");

            if (_lastAuditReport.estimatedTotalBandwidthGbpsAt60Fps > 4.5f)
            {
                EditorGUILayout.HelpBox(
                    "WARNING: Estimated RenderTexture DRAM write/read traffic exceeds 4.5 GB/s at 60 FPS. On mid-range mobile SoCs, external memory bandwidth above ~4.5 GB/s causes severe battery drain and thermal throttling within 8-12 minutes.",
                    MessageType.Warning);
            }

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (var f in _lastAuditReport.findings)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField(
                    $"{f.textureName} ({f.width}x{f.height}, MSAA {f.antiAliasing}x, {f.colorFormat})",
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"Est. Tile Flush Traffic: {f.estimatedTileStoreMegabytesPerFrame:F2} MB/frame ({(f.estimatedTileStoreMegabytesPerFrame * 60f / 1024f):F2} GB/s | ~{f.estimatedDramPowerMilliwattsAt60Fps:F0} mW @ 60 FPS)");
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

        private void RecalculatePipelineSimulation()
        {
            _pipelineEstimate = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                _simWidth,
                _simHeight,
                _simMsaa,
                _simColorFormat,
                _simDepthTex,
                _simOpaqueTex,
                _simBloom,
                _simShadowRes,
                _simSocTier);
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

        private void ExportLiveReports()
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
                string mdPath = Path.ChangeExtension(path, ".md");
                File.WriteAllText(mdPath, activeHud.Telemetry.ExportSummaryToMarkdown(_simulatedTargetFps), Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
        }
    }
}

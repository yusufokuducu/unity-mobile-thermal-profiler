using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UIElements;

namespace YusufOkuducu.MobileThermalProfiler.Editor
{
    public sealed class ThermalBandwidthProfilerWindow : EditorWindow
    {
        private TbdrAuditReport _lastAuditReport;
        private UrpPipelineBandwidthEstimate _pipelineEstimate;
        private Vector2 _scrollPos;
        private float _simulatedTargetFps = 60f;
        private string _searchFilter = string.Empty;

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

        // UI Toolkit containers
        private VisualElement _heatmapContainer;
        private VisualElement _pipelineContainer;
        private ScrollView _rtFindingsScroll;

        public TbdrAuditReport LastAuditReport => _lastAuditReport;
        public UrpPipelineBandwidthEstimate CurrentPipelineEstimate => _pipelineEstimate;
        public string SearchFilter => _searchFilter;

        [MenuItem("Window/Analysis/Mobile Thermal & TBDR Bandwidth Profiler")]
        public static void ShowWindow()
        {
            var win = GetWindow<ThermalBandwidthProfilerWindow>("Mobile TBDR & Thermal");
            win.minSize = new Vector2(700f, 540f);
            win.Show();
        }

        private void OnEnable()
        {
            RunTbdrAudit();
            RecalculatePipelineSimulation();
        }

        public void CreateGUI()
        {
            if (_lastAuditReport == null)
                RunTbdrAudit();
            if (_pipelineEstimate.passBreakdown == null)
                RecalculatePipelineSimulation();

            VisualElement root = rootVisualElement;
            if (root == null)
                return;

            root.Clear();
            root.style.paddingTop = 8f;
            root.style.paddingBottom = 8f;
            root.style.paddingLeft = 10f;
            root.style.paddingRight = 10f;

            var titleLabel = new Label("Mobile Thermal, Frame-Pacing & TBDR Bandwidth Auditor")
            {
                name = "header-title"
            };
            titleLabel.style.fontSize = 15f;
            titleLabel.style.marginBottom = 4f;
            root.Add(titleLabel);

            var infoBox = new HelpBox(
                "Audits active RenderTextures & URP camera pipelines for Tile-Based Deferred Rendering (Mali / Adreno / Apple GPU) DRAM bandwidth hazards, estimates mW memory power draw, and inspects live MobileThermalHudOverlay telemetry.",
                HelpBoxMessageType.Info);
            infoBox.style.marginBottom = 8f;
            root.Add(infoBox);

            // Action Toolbar
            var toolbarRow = new VisualElement { name = "action-toolbar" };
            toolbarRow.style.flexDirection = FlexDirection.Row;
            toolbarRow.style.marginBottom = 8f;

            var auditBtn = new Button(() =>
            {
                RunTbdrAudit();
                RefreshUiToolkitView();
            })
            {
                name = "btn-audit-rt",
                text = "Audit Active RenderTextures"
            };
            auditBtn.style.height = 28f;
            auditBtn.style.flexGrow = 1f;
            auditBtn.style.marginRight = 4f;

            var autoTuneBtn = new Button(() =>
            {
                ApplyPipelineAutoTune();
                RefreshUiToolkitView();
            })
            {
                name = "btn-autotune-urp",
                text = "Auto-Tune URP for SoC Budget"
            };
            autoTuneBtn.style.height = 28f;
            autoTuneBtn.style.flexGrow = 1f;
            autoTuneBtn.style.marginRight = 4f;

            var hudBtn = new Button(AddHudToScene)
            {
                name = "btn-add-hud",
                text = "Add Runtime HUD to Scene"
            };
            hudBtn.style.height = 28f;
            hudBtn.style.flexGrow = 1f;
            hudBtn.style.marginRight = 4f;

            var exportBtn = new Button(ExportLiveReports)
            {
                name = "btn-export",
                text = "Export CSV / MD / MCP JSON"
            };
            exportBtn.style.height = 28f;
            exportBtn.style.flexGrow = 1f;

            toolbarRow.Add(auditBtn);
            toolbarRow.Add(autoTuneBtn);
            toolbarRow.Add(hudBtn);
            toolbarRow.Add(exportBtn);
            root.Add(toolbarRow);

            // Search Filter
            var searchField = new TextField("Filter RenderTextures / Recommendations")
            {
                name = "search-field",
                value = _searchFilter
            };
            searchField.style.marginBottom = 8f;
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? string.Empty;
                RefreshUiToolkitView();
            });
            root.Add(searchField);

            _heatmapContainer = new VisualElement { name = "heatmap-container" };
            _heatmapContainer.style.marginBottom = 8f;
            root.Add(_heatmapContainer);

            _pipelineContainer = new VisualElement { name = "pipeline-container" };
            _pipelineContainer.style.marginBottom = 8f;
            root.Add(_pipelineContainer);

            _rtFindingsScroll = new ScrollView { name = "rt-findings-scroll" };
            _rtFindingsScroll.style.flexGrow = 1f;
            root.Add(_rtFindingsScroll);

            RefreshUiToolkitView();
        }

        public void SetCustomAuditReport(TbdrAuditReport report, UrpPipelineBandwidthEstimate? customPipeline = null)
        {
            _lastAuditReport = report ?? new TbdrAuditReport();
            if (customPipeline.HasValue)
                _pipelineEstimate = customPipeline.Value;
            RefreshUiToolkitView();
        }

        public void SetSearchFilter(string query)
        {
            _searchFilter = query ?? string.Empty;
            RefreshUiToolkitView();
        }

        public void ConfigurePipelineSimulator(
            int width,
            int height,
            int msaa,
            GraphicsFormat colorFormat,
            bool depthTex,
            bool opaqueTex,
            bool bloom,
            int shadowRes,
            MobileSoCTier tier)
        {
            _simWidth = width;
            _simHeight = height;
            _simMsaa = msaa;
            _simColorFormat = colorFormat;
            _simDepthTex = depthTex;
            _simOpaqueTex = opaqueTex;
            _simBloom = bloom;
            _simShadowRes = shadowRes;
            _simSocTier = tier;
            RecalculatePipelineSimulation();
            RefreshUiToolkitView();
        }

        public UrpPipelineAutoTuneReport ApplyPipelineAutoTune()
        {
            UrpPipelineAutoTuneReport report = MobileThermalProfilerMcpBridge.AutoTuneUrpPipelineForSoCTier(
                _simWidth, _simHeight, _simMsaa, _simColorFormat,
                _simDepthTex, _simOpaqueTex, _simBloom, _simShadowRes, _simSocTier);

            _simColorFormat = report.tunedColorFormat;
            _simDepthTex = report.tunedDepthTextureEnabled;
            _simOpaqueTex = report.tunedOpaqueTextureEnabled;
            _simBloom = report.tunedBloomEnabled;
            _simShadowRes = report.tunedShadowResolution;
            _pipelineEstimate = report.afterEstimate;
            return report;
        }

        public List<RenderTextureBandwidthFinding> GetFilteredFindings()
        {
            var list = new List<RenderTextureBandwidthFinding>();
            if (_lastAuditReport == null || _lastAuditReport.findings == null)
                return list;

            string q = (_searchFilter ?? string.Empty).Trim();
            for (int i = 0; i < _lastAuditReport.findings.Count; i++)
            {
                RenderTextureBandwidthFinding f = _lastAuditReport.findings[i];
                if (!string.IsNullOrEmpty(q))
                {
                    bool match =
                        (!string.IsNullOrEmpty(f.textureName) && f.textureName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(f.recommendation) && f.recommendation.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        f.colorFormat.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                        continue;
                }
                list.Add(f);
            }
            return list;
        }

        public void RefreshUiToolkitView()
        {
            if (_heatmapContainer == null || _pipelineContainer == null || _rtFindingsScroll == null)
                return;

            _heatmapContainer.Clear();
            _pipelineContainer.Clear();
            _rtFindingsScroll.Clear();

            ThermalBandwidthHeatmapSummary heatmap = MobileThermalProfilerMcpBridge.ComputeBandwidthHeatmap(
                _lastAuditReport, _pipelineEstimate, _simSocTier);

            _heatmapContainer.Add(BuildBandwidthHeatmapCard(heatmap));
            _pipelineContainer.Add(BuildPipelineSimulationCard(_pipelineEstimate, _simSocTier));

            List<RenderTextureBandwidthFinding> filtered = GetFilteredFindings();
            for (int i = 0; i < filtered.Count; i++)
            {
                _rtFindingsScroll.Add(BuildRenderTextureFindingCard(filtered[i]));
            }
        }

        public static VisualElement BuildBandwidthHeatmapCard(ThermalBandwidthHeatmapSummary h)
        {
            var card = new VisualElement { name = "bandwidth-heatmap-card" };
            card.style.paddingTop = 8f;
            card.style.paddingBottom = 8f;
            card.style.paddingLeft = 10f;
            card.style.paddingRight = 10f;
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = GetThermalBudgetColor(h.budgetUtilizationRatio);
            card.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);

            var title = new Label(
                $"LPDDR Bandwidth & Thermal Envelope Heatmap [{h.socTier}] — Budget: {h.tierSustainedBudgetGbps:F1} GB/s")
            {
                name = "heatmap-title"
            };
            title.style.marginBottom = 4f;
            card.Add(title);

            var utilBar = new ProgressBar
            {
                name = "thermal-budget-gauge",
                lowValue = 0f,
                highValue = 100f,
                value = Mathf.Clamp01(h.budgetUtilizationRatio / 1.5f) * 100f,
                title = $"Pipeline Traffic: {h.pipelineBandwidthGbpsAt60Fps:F2} GB/s ({(h.budgetUtilizationRatio * 100f):F0}% of {h.tierSustainedBudgetGbps:F1} GB/s budget | ~{h.pipelinePowerMilliwattsAt60Fps:F0} mW DRAM Power)"
            };
            utilBar.style.marginBottom = 4f;
            card.Add(utilBar);

            var summaryRow = new Label(
                $"Allocated RTs: {h.totalRenderTexturesAudited} ({h.renderTextureBandwidthGbpsAt60Fps:F2} GB/s) | " +
                $"Unresolved MSAA Hazards: {h.unresolvedMsaaCount} | Persistent Depth Stores: {h.persistentDepthStoreCount} | 64-Bit HDR Buffers: {h.heavy64BitHdrCount}");
            summaryRow.style.fontSize = 11f;
            card.Add(summaryRow);

            return card;
        }

        private static VisualElement BuildPipelineSimulationCard(UrpPipelineBandwidthEstimate est, MobileSoCTier tier)
        {
            var card = new VisualElement { name = "urp-pipeline-card" };
            card.style.paddingTop = 6f;
            card.style.paddingBottom = 6f;
            card.style.paddingLeft = 10f;
            card.style.paddingRight = 10f;
            card.style.backgroundColor = new Color(0.19f, 0.19f, 0.21f, 1f);

            var header = new Label(
                $"URP Camera Pipeline Breakdown ({est.width}x{est.height}, MSAA {est.msaaSamples}x): " +
                $"{est.totalMegabytesPerFrame:F2} MB/frame | {est.bandwidthGbpsAt60Fps:F2} GB/s @ 60 FPS | {est.bandwidthGbpsAt120Fps:F2} GB/s @ 120 FPS");
            header.style.marginBottom = 4f;
            card.Add(header);

            if (est.passBreakdown != null)
            {
                for (int i = 0; i < est.passBreakdown.Count; i++)
                {
                    var passLabel = new Label("• " + est.passBreakdown[i]);
                    passLabel.style.fontSize = 11f;
                    card.Add(passLabel);
                }
            }

            if (est.exceedsTierThermalEnvelope)
            {
                var warn = new HelpBox(
                    $"THERMAL ENVELOPE EXCEEDED: {est.bandwidthGbpsAt60Fps:F2} GB/s exceeds the sustained budget ({TbdrBandwidthAuditor.GetSustainedBandwidthBudgetGbps(tier):F1} GB/s) for {tier}. Click 'Auto-Tune URP for SoC Budget' to remediate.",
                    HelpBoxMessageType.Warning);
                warn.style.marginTop = 4f;
                card.Add(warn);
            }

            return card;
        }

        private static VisualElement BuildRenderTextureFindingCard(RenderTextureBandwidthFinding f)
        {
            var card = new VisualElement { name = $"rt-card-{f.textureName}" };
            card.style.marginBottom = 6f;
            card.style.paddingTop = 6f;
            card.style.paddingBottom = 6f;
            card.style.paddingLeft = 8f;
            card.style.paddingRight = 8f;
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = f.hasUnresolvedMultisamplePenalty
                ? new Color(0.88f, 0.24f, 0.24f, 1f)
                : (f.hasDepthStorePenalty ? new Color(0.92f, 0.62f, 0.16f, 1f) : new Color(0.24f, 0.76f, 0.42f, 1f));
            card.style.backgroundColor = new Color(0.21f, 0.21f, 0.23f, 1f);

            var title = new Label($"{f.textureName} ({f.width}x{f.height}, MSAA {f.antiAliasing}x, {f.colorFormat})");
            card.Add(title);

            float gbps60 = (f.estimatedTileStoreMegabytesPerFrame * 60f) / 1024f;
            var trafficBar = new ProgressBar
            {
                lowValue = 0f,
                highValue = 4.5f,
                value = Math.Min(4.5f, gbps60),
                title = $"Tile Flush: {f.estimatedTileStoreMegabytesPerFrame:F2} MB/frame ({gbps60:F2} GB/s | ~{f.estimatedDramPowerMilliwattsAt60Fps:F0} mW @ 60 FPS)"
            };
            trafficBar.style.marginTop = 3f;
            trafficBar.style.marginBottom = 3f;
            card.Add(trafficBar);

            var recLabel = new Label($"Recommendation: {f.recommendation}");
            recLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Add(recLabel);

            return card;
        }

        public static Color GetThermalBudgetColor(float utilizationRatio)
        {
            if (utilizationRatio > 1.0f)
                return new Color(0.88f, 0.24f, 0.24f, 1f);
            if (utilizationRatio > 0.8f)
                return new Color(0.92f, 0.62f, 0.16f, 1f);
            return new Color(0.24f, 0.76f, 0.42f, 1f);
        }

        private void OnGUI()
        {
            if (rootVisualElement != null && rootVisualElement.childCount > 0)
                return;

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

            if (GUILayout.Button("Export Live Telemetry CSV, MD & MCP JSON", GUILayout.Height(28f)))
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
            foreach (var f in GetFilteredFindings())
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
            string path = EditorUtility.SaveFilePanel("Export Mobile Thermal & TBDR Report", "", "mobile_thermal_report.md", "md");
            if (string.IsNullOrEmpty(path))
                return;

            if (_lastAuditReport == null)
                RunTbdrAudit();

            var activeHud = FindFirstObjectByType<MobileThermalHudOverlay>();
            TelemetrySummary? liveSum = (activeHud != null && activeHud.Telemetry != null && activeHud.Telemetry.Count > 0)
                ? activeHud.Telemetry.ComputeSummary(_simulatedTargetFps)
                : (TelemetrySummary?)null;

            File.WriteAllText(path, TbdrBandwidthAuditor.ExportTbdrAuditToMarkdown(_lastAuditReport, _pipelineEstimate), Encoding.UTF8);

            string jsonPath = Path.ChangeExtension(path, ".json");
            File.WriteAllText(jsonPath, MobileThermalProfilerMcpBridge.ExportMcpReportToJson(_lastAuditReport, _pipelineEstimate, _simSocTier, liveSum), Encoding.UTF8);

            if (activeHud != null && activeHud.Telemetry != null && activeHud.Telemetry.Count > 0)
            {
                string csvPath = Path.ChangeExtension(path, ".csv");
                activeHud.Telemetry.SaveCsvToFile(csvPath);
            }

            EditorUtility.RevealInFinder(path);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace YusufOkuducu.MobileThermalProfiler.Editor
{
    [Serializable]
    public struct ThermalBandwidthHeatmapSummary
    {
        public MobileSoCTier socTier;
        public float tierSustainedBudgetGbps;
        public float pipelineBandwidthGbpsAt60Fps;
        public float pipelinePowerMilliwattsAt60Fps;
        public float budgetUtilizationRatio;
        public bool exceedsTierThermalEnvelope;
        public int totalRenderTexturesAudited;
        public int unresolvedMsaaCount;
        public int persistentDepthStoreCount;
        public int heavy64BitHdrCount;
        public float renderTextureBandwidthGbpsAt60Fps;
    }

    [Serializable]
    public sealed class RenderTextureAutoFixReport
    {
        public int totalInspected;
        public int modifiedCount;
        public float bandwidthGbpsBefore;
        public float bandwidthGbpsAfter;
        public float dramPowerMilliwattsSavedAt60Fps;
        public List<string> appliedFixes = new List<string>();
    }

    [Serializable]
    public sealed class UrpPipelineAutoTuneReport
    {
        public MobileSoCTier socTier;
        public float targetBudgetGbps;
        public bool modified;
        public UrpPipelineBandwidthEstimate beforeEstimate;
        public UrpPipelineBandwidthEstimate afterEstimate;
        public GraphicsFormat tunedColorFormat;
        public bool tunedDepthTextureEnabled;
        public bool tunedOpaqueTextureEnabled;
        public bool tunedBloomEnabled;
        public int tunedShadowResolution;
        public float dramPowerMilliwattsSavedAt60Fps;
        public List<string> appliedOptimizations = new List<string>();
    }

    /// <summary>
    /// Model Context Protocol (MCP) & AI Agent JSON-RPC 2.0 Bridge for Mobile Thermal & TBDR Bandwidth Profiler.
    /// Allows AI coding assistants (Cursor, Claude, Antigravity, Unity MCP servers) and CI pipelines
    /// to query TBDR memory bandwidth, LPDDR DRAM power draw, frame-pacing telemetry, and invoke automated optimizations.
    /// </summary>
    public static class MobileThermalProfilerMcpBridge
    {
        public const string ServerName = "unity-mobile-thermal-profiler";
        public const string PackageVersion = "1.2.0";
        public const string McpProtocolVersion = "2024-11-05";

        public static ThermalBandwidthHeatmapSummary ComputeBandwidthHeatmap(
            TbdrAuditReport rtReport,
            UrpPipelineBandwidthEstimate pipelineEst,
            MobileSoCTier socTier = MobileSoCTier.MidRangeMainstream)
        {
            float budgetGbps = TbdrBandwidthAuditor.GetSustainedBandwidthBudgetGbps(socTier);
            var h = new ThermalBandwidthHeatmapSummary
            {
                socTier = socTier,
                tierSustainedBudgetGbps = budgetGbps,
                pipelineBandwidthGbpsAt60Fps = pipelineEst.bandwidthGbpsAt60Fps,
                pipelinePowerMilliwattsAt60Fps = pipelineEst.estimatedDramPowerMilliwattsAt60Fps,
                budgetUtilizationRatio = budgetGbps > 0.01f ? (pipelineEst.bandwidthGbpsAt60Fps / budgetGbps) : 0f,
                exceedsTierThermalEnvelope = pipelineEst. bandwidthGbpsAt60Fps > budgetGbps
            };

            if (rtReport != null)
            {
                h.totalRenderTexturesAudited = rtReport.totalRenderTexturesAudited;
                h.renderTextureBandwidthGbpsAt60Fps = rtReport.estimatedTotalBandwidthGbpsAt60Fps;
                if (rtReport.findings != null)
                {
                    for (int i = 0; i < rtReport.findings.Count; i++)
                    {
                        RenderTextureBandwidthFinding f = rtReport.findings[i];
                        if (f.hasUnresolvedMultisamplePenalty)
                            h.unresolvedMsaaCount++;
                        if (f.hasDepthStorePenalty)
                            h.persistentDepthStoreCount++;
                        if (TbdrBandwidthAuditor.GetBytesPerPixel(f.colorFormat) >= 8)
                            h.heavy64BitHdrCount++;
                    }
                }
            }

            return h;
        }

        /// <summary>
        /// Automatically fixes TBDR memory bandwidth hazards on RenderTextures:
        /// 1. Disables bindTextureMS on MSAA targets so tile memory resolves on-chip.
        /// 2. Downgrades 64-bit/128-bit float color formats to 32-bit B10G11R11_UFloatPack32 (R11G11B10 HDR).
        /// 3. Disables per-frame autoGenerateMips on dynamic RenderTextures.
        /// </summary>
        public static RenderTextureAutoFixReport AutoFixRenderTextures(IReadOnlyList<RenderTexture> textures)
        {
            var report = new RenderTextureAutoFixReport();
            if (textures == null || textures.Count == 0)
                return report;

            TbdrAuditReport before = TbdrBandwidthAuditor.AuditRenderTextures(textures);
            report.totalInspected = before.totalRenderTexturesAudited;
            report.bandwidthGbpsBefore = before.estimatedTotalBandwidthGbpsAt60Fps;

            for (int i = 0; i < textures.Count; i++)
            {
                RenderTexture rt = textures[i];
                if (rt == null || rt.width <= 0 || rt.height <= 0)
                    continue;

                bool touched = false;
                string rtName = string.IsNullOrEmpty(rt.name) ? $"RenderTexture[{i}]" : rt.name;

                if (rt.bindTextureMS && rt.antiAliasing > 1)
                {
                    rt.bindTextureMS = false;
                    touched = true;
                    report.appliedFixes.Add(
                        $"{rtName}: Disabled bindTextureMS ({rt.antiAliasing}x MSAA) to keep multisample resolve inside on-chip tile SRAM.");
                }

                if (TbdrBandwidthAuditor.GetBytesPerPixel(rt.graphicsFormat) >= 8)
                {
                    GraphicsFormat oldFmt = rt.graphicsFormat;
                    rt.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                    touched = true;
                    report.appliedFixes.Add(
                        $"{rtName}: Converted HDR color format from {oldFmt} to B10G11R11_UFloatPack32 (-50% color DRAM bandwidth).");
                }

                if (rt.useMipMap && rt.autoGenerateMips)
                {
                    rt.autoGenerateMips = false;
                    touched = true;
                    report.appliedFixes.Add(
                        $"{rtName}: Disabled autoGenerateMips to eliminate per-frame mipmap DRAM read/write chain.");
                }

                if (touched)
                    report.modifiedCount++;
            }

            TbdrAuditReport after = TbdrBandwidthAuditor.AuditRenderTextures(textures);
            report.bandwidthGbpsAfter = after.estimatedTotalBandwidthGbpsAt60Fps;
            report.dramPowerMilliwattsSavedAt60Fps = Math.Max(0f, before.estimatedDramPowerMilliwattsAt60Fps - after.estimatedDramPowerMilliwattsAt60Fps);

            return report;
        }

        /// <summary>
        /// Automatically tunes a URP mobile camera configuration so its external LPDDR DRAM traffic
        /// fits within the target MobileSoCTier sustained thermal envelope.
        /// </summary>
        public static UrpPipelineAutoTuneReport AutoTuneUrpPipelineForSoCTier(
            int width,
            int height,
            int msaaSamples,
            GraphicsFormat colorFormat,
            bool cameraDepthTextureEnabled,
            bool cameraOpaqueTextureEnabled,
            bool postProcessingBloomEnabled,
            int shadowmapResolution = 2048,
            MobileSoCTier socTier = MobileSoCTier.MidRangeMainstream)
        {
            UrpPipelineBandwidthEstimate before = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                width, height, msaaSamples, colorFormat,
                cameraDepthTextureEnabled, cameraOpaqueTextureEnabled, postProcessingBloomEnabled,
                shadowmapResolution, socTier);

            float budgetGbps = TbdrBandwidthAuditor.GetSustainedBandwidthBudgetGbps(socTier);
            var report = new UrpPipelineAutoTuneReport
            {
                socTier = socTier,
                targetBudgetGbps = budgetGbps,
                beforeEstimate = before,
                afterEstimate = before,
                tunedColorFormat = colorFormat,
                tunedDepthTextureEnabled = cameraDepthTextureEnabled,
                tunedOpaqueTextureEnabled = cameraOpaqueTextureEnabled,
                tunedBloomEnabled = postProcessingBloomEnabled,
                tunedShadowResolution = shadowmapResolution
            };

            if (!before.exceedsTierThermalEnvelope)
                return report;

            // Step 1: Switch 64-bit+ HDR color buffer to 32-bit B10G11R11_UFloatPack32
            if (TbdrBandwidthAuditor.GetBytesPerPixel(report.tunedColorFormat) >= 8)
            {
                report.tunedColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                report.appliedOptimizations.Add(
                    "Switched URP HDR color format from 64-bit to 32-bit B10G11R11_UFloatPack32 (-50% color tile flush traffic).");
            }

            // Step 2: Disable _CameraOpaqueTexture mid-frame tile flush
            UrpPipelineBandwidthEstimate current = Recalc(width, height, msaaSamples, report);
            if (current.exceedsTierThermalEnvelope && report.tunedOpaqueTextureEnabled)
            {
                report.tunedOpaqueTextureEnabled = false;
                report.appliedOptimizations.Add(
                    "Disabled _CameraOpaqueTexture to prevent mid-frame TBDR tile flush and DRAM reload.");
                current = Recalc(width, height, msaaSamples, report);
            }

            // Step 3: Disable _CameraDepthTexture resolve & copy
            if (current.exceedsTierThermalEnvelope && report.tunedDepthTextureEnabled)
            {
                report.tunedDepthTextureEnabled = false;
                report.appliedOptimizations.Add(
                    "Disabled _CameraDepthTexture copy to keep depth buffer memoryless in on-chip tile SRAM.");
                current = Recalc(width, height, msaaSamples, report);
            }

            // Step 4: Downscale shadowmap atlas if still exceeding budget
            if (current.exceedsTierThermalEnvelope && report.tunedShadowResolution > 1024)
            {
                report.tunedShadowResolution = 1024;
                report.appliedOptimizations.Add(
                    "Reduced Shadowmap Atlas resolution to 1024x1024 to cut shadow depth write + PCF read traffic.");
                current = Recalc(width, height, msaaSamples, report);
            }

            // Step 5: Disable post-processing bloom pyramid if entry-level budget still exceeded
            if (current.exceedsTierThermalEnvelope && report.tunedBloomEnabled)
            {
                report.tunedBloomEnabled = false;
                report.appliedOptimizations.Add(
                    "Disabled Post-Processing Bloom pyramid to fit strict SoC DRAM thermal envelope.");
                current = Recalc(width, height, msaaSamples, report);
            }

            report.afterEstimate = current;
            report.modified = report.appliedOptimizations.Count > 0;
            report.dramPowerMilliwattsSavedAt60Fps = Math.Max(0f, before.estimatedDramPowerMilliwattsAt60Fps - current.estimatedDramPowerMilliwattsAt60Fps);
            return report;
        }

        private static UrpPipelineBandwidthEstimate Recalc(int w, int h, int msaa, UrpPipelineAutoTuneReport r)
        {
            return TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                w, h, msaa,
                r.tunedColorFormat,
                r.tunedDepthTextureEnabled,
                r.tunedOpaqueTextureEnabled,
                r.tunedBloomEnabled,
                r.tunedShadowResolution,
                r.socTier);
        }

        public static string ExportMcpReportToJson(
            TbdrAuditReport rtReport,
            UrpPipelineBandwidthEstimate pipelineEst,
            MobileSoCTier socTier = MobileSoCTier.MidRangeMainstream,
            TelemetrySummary? liveTelemetry = null)
        {
            ThermalBandwidthHeatmapSummary heatmap = ComputeBandwidthHeatmap(rtReport, pipelineEst, socTier);
            var sb = new StringBuilder(2048);
            sb.Append('{');
            AppendJsonProp(sb, "tool", ServerName, true);
            AppendJsonProp(sb, "version", PackageVersion, true);
            sb.Append("\"heatmap\":");
            AppendHeatmapJson(sb, heatmap);
            sb.Append(",\"pipelineSimulation\":");
            AppendPipelineEstimateJson(sb, pipelineEst);
            sb.Append(",\"renderTextureAudit\":");
            AppendRtReportJson(sb, rtReport ?? new TbdrAuditReport());
            if (liveTelemetry.HasValue)
            {
                sb.Append(",\"liveTelemetry\":");
                AppendTelemetrySummaryJson(sb, liveTelemetry.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Processes a JSON-RPC 2.0 request according to the Model Context Protocol (MCP) specification.
        /// Supported methods: "initialize", "tools/list", "tools/call".
        /// </summary>
        public static string HandleJsonRpcRequest(string jsonRpcRequest)
        {
            if (string.IsNullOrWhiteSpace(jsonRpcRequest))
                return BuildJsonRpcError("null", -32700, "Parse error: Empty JSON-RPC request.");

            string trimmed = jsonRpcRequest.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return BuildJsonRpcError("null", -32700, "Parse error: Malformed JSON object.");

            string idRaw = ExtractJsonRpcIdRaw(trimmed);
            string method = ExtractJsonStringField(trimmed, "method");

            if (string.IsNullOrEmpty(method))
                return BuildJsonRpcError(idRaw, -32600, "Invalid Request: Missing 'method' field.");

            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                var res = new StringBuilder();
                res.Append("{\"protocolVersion\":\"").Append(McpProtocolVersion).Append("\",");
                res.Append("\"capabilities\":{\"tools\":{\"listChanged\":false}},");
                res.Append("\"serverInfo\":{\"name\":\"").Append(ServerName).Append("\",\"version\":\"").Append(PackageVersion).Append("\"}}");
                return BuildJsonRpcResult(idRaw, res.ToString());
            }

            if (string.Equals(method, "tools/list", StringComparison.Ordinal))
            {
                return BuildJsonRpcResult(idRaw, BuildToolsListJson());
            }

            if (string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                string toolName = ExtractJsonStringField(trimmed, "name");
                if (string.IsNullOrEmpty(toolName))
                    return BuildJsonRpcError(idRaw, -32602, "Invalid params: Missing tool 'name' in tools/call.");

                if (string.Equals(toolName, "thermal_profiler.audit_render_textures", StringComparison.Ordinal))
                {
                    TbdrAuditReport rtReport = TbdrBandwidthAuditor.AuditActiveRenderTextures();
                    var sb = new StringBuilder();
                    AppendRtReportJson(sb, rtReport);
                    return BuildJsonRpcToolCallResult(idRaw, sb.ToString());
                }

                if (string.Equals(toolName, "thermal_profiler.simulate_urp_pipeline", StringComparison.Ordinal))
                {
                    ParseUrpParams(trimmed, out int w, out int h, out int msaa, out GraphicsFormat fmt,
                        out bool depth, out bool opaque, out bool bloom, out int shadow, out MobileSoCTier tier);

                    UrpPipelineBandwidthEstimate est = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                        w, h, msaa, fmt, depth, opaque, bloom, shadow, tier);
                    var sb = new StringBuilder();
                    AppendPipelineEstimateJson(sb, est);
                    return BuildJsonRpcToolCallResult(idRaw, sb.ToString());
                }

                if (string.Equals(toolName, "thermal_profiler.autotune_urp_pipeline", StringComparison.Ordinal))
                {
                    ParseUrpParams(trimmed, out int w, out int h, out int msaa, out GraphicsFormat fmt,
                        out bool depth, out bool opaque, out bool bloom, out int shadow, out MobileSoCTier tier);

                    UrpPipelineAutoTuneReport tune = AutoTuneUrpPipelineForSoCTier(
                        w, h, msaa, fmt, depth, opaque, bloom, shadow, tier);
                    string payload = SerializeAutoTuneReportJson(tune);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "thermal_profiler.autofix_render_textures", StringComparison.Ordinal))
                {
                    RenderTexture[] active = Resources.FindObjectsOfTypeAll<RenderTexture>();
                    RenderTextureAutoFixReport fix = AutoFixRenderTextures(active);
                    string payload = SerializeRtAutoFixReportJson(fix);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                return BuildJsonRpcError(idRaw, -32602, $"Unknown tool '{EscapeJson(toolName)}'.");
            }

            return BuildJsonRpcError(idRaw, -32601, $"Method not found: '{EscapeJson(method)}'.");
        }

        private static void ParseUrpParams(
            string json,
            out int width,
            out int height,
            out int msaa,
            out GraphicsFormat format,
            out bool depthTex,
            out bool opaqueTex,
            out bool bloom,
            out int shadowRes,
            out MobileSoCTier tier)
        {
            width = ExtractJsonIntField(json, "width", 2400);
            height = ExtractJsonIntField(json, "height", 1080);
            msaa = ExtractJsonIntField(json, "msaaSamples", 4);
            shadowRes = ExtractJsonIntField(json, "shadowmapResolution", 2048);
            depthTex = ExtractJsonBoolField(json, "cameraDepthTextureEnabled", false);
            opaqueTex = ExtractJsonBoolField(json, "cameraOpaqueTextureEnabled", false);
            bloom = ExtractJsonBoolField(json, "postProcessingBloomEnabled", true);

            string fmtStr = ExtractJsonStringField(json, "colorFormat");
            if (!string.IsNullOrEmpty(fmtStr) && Enum.TryParse(fmtStr, true, out GraphicsFormat parsedFmt))
                format = parsedFmt;
            else
                format = GraphicsFormat.B10G11R11_UFloatPack32;

            string tierStr = ExtractJsonStringField(json, "socTier");
            if (!string.IsNullOrEmpty(tierStr) && Enum.TryParse(tierStr, true, out MobileSoCTier parsedTier))
                tier = parsedTier;
            else
                tier = MobileSoCTier.MidRangeMainstream;
        }

        public static string SerializeAutoTuneReportJson(UrpPipelineAutoTuneReport r)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProp(sb, "socTier", r.socTier.ToString(), true);
            AppendJsonProp(sb, "targetBudgetGbps", r.targetBudgetGbps, true);
            AppendJsonProp(sb, "modified", r.modified, true);
            AppendJsonProp(sb, "bandwidthGbpsBefore", r.beforeEstimate.bandwidthGbpsAt60Fps, true);
            AppendJsonProp(sb, "bandwidthGbpsAfter", r.afterEstimate.bandwidthGbpsAt60Fps, true);
            AppendJsonProp(sb, "dramPowerMilliwattsSavedAt60Fps", r.dramPowerMilliwattsSavedAt60Fps, true);
            AppendJsonProp(sb, "exceedsTierThermalEnvelopeAfter", r.afterEstimate.exceedsTierThermalEnvelope, true);
            AppendJsonProp(sb, "tunedColorFormat", r.tunedColorFormat.ToString(), true);
            AppendJsonProp(sb, "tunedDepthTextureEnabled", r.tunedDepthTextureEnabled, true);
            AppendJsonProp(sb, "tunedOpaqueTextureEnabled", r.tunedOpaqueTextureEnabled, true);
            AppendJsonProp(sb, "tunedBloomEnabled", r.tunedBloomEnabled, true);
            AppendJsonProp(sb, "tunedShadowResolution", r.tunedShadowResolution, true);
            sb.Append("\"appliedOptimizations\":[");
            if (r.appliedOptimizations != null)
            {
                for (int i = 0; i < r.appliedOptimizations.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedOptimizations[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializeRtAutoFixReportJson(RenderTextureAutoFixReport r)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonProp(sb, "totalInspected", r.totalInspected, true);
            AppendJsonProp(sb, "modifiedCount", r.modifiedCount, true);
            AppendJsonProp(sb, "bandwidthGbpsBefore", r.bandwidthGbpsBefore, true);
            AppendJsonProp(sb, "bandwidthGbpsAfter", r.bandwidthGbpsAfter, true);
            AppendJsonProp(sb, "dramPowerMilliwattsSavedAt60Fps", r.dramPowerMilliwattsSavedAt60Fps, true);
            sb.Append("\"appliedFixes\":[");
            if (r.appliedFixes != null)
            {
                for (int i = 0; i < r.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildToolsListJson()
        {
            return "{\"tools\":[" +
                "{\"name\":\"thermal_profiler.audit_render_textures\",\"description\":\"Audits allocated RenderTextures for TBDR (Mali/Adreno/Apple GPU) tile flush memory bandwidth (GB/s) and LPDDR DRAM power draw (mW).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"thermal_profiler.simulate_urp_pipeline\",\"description\":\"Simulates URP mobile camera pipeline bandwidth and DRAM power draw against target SoC thermal envelopes.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\"},\"height\":{\"type\":\"integer\"},\"msaaSamples\":{\"type\":\"integer\"},\"colorFormat\":{\"type\":\"string\"},\"cameraDepthTextureEnabled\":{\"type\":\"boolean\"},\"cameraOpaqueTextureEnabled\":{\"type\":\"boolean\"},\"postProcessingBloomEnabled\":{\"type\":\"boolean\"},\"shadowmapResolution\":{\"type\":\"integer\"},\"socTier\":{\"type\":\"string\"}}}}," +
                "{\"name\":\"thermal_profiler.autotune_urp_pipeline\",\"description\":\"Automatically tunes URP camera settings (R11G11B10 HDR format, depth/opaque copy disabling, shadow atlas scaling) to fit inside the target MobileSoCTier sustained DRAM thermal envelope.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\"},\"height\":{\"type\":\"integer\"},\"msaaSamples\":{\"type\":\"integer\"},\"colorFormat\":{\"type\":\"string\"},\"cameraDepthTextureEnabled\":{\"type\":\"boolean\"},\"cameraOpaqueTextureEnabled\":{\"type\":\"boolean\"},\"postProcessingBloomEnabled\":{\"type\":\"boolean\"},\"shadowmapResolution\":{\"type\":\"integer\"},\"socTier\":{\"type\":\"string\"}}}}," +
                "{\"name\":\"thermal_profiler.autofix_render_textures\",\"description\":\"Automatically optimizes active RenderTextures by disabling bindTextureMS, switching 64-bit HDR buffers to B10G11R11_UFloatPack32, and disabling per-frame autoGenerateMips.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}" +
                "]}";
        }

        private static void AppendHeatmapJson(StringBuilder sb, ThermalBandwidthHeatmapSummary h)
        {
            sb.Append('{');
            AppendJsonProp(sb, "socTier", h.socTier.ToString(), true);
            AppendJsonProp(sb, "tierSustainedBudgetGbps", h.tierSustainedBudgetGbps, true);
            AppendJsonProp(sb, "pipelineBandwidthGbpsAt60Fps", h.pipelineBandwidthGbpsAt60Fps, true);
            AppendJsonProp(sb, "pipelinePowerMilliwattsAt60Fps", h.pipelinePowerMilliwattsAt60Fps, true);
            AppendJsonProp(sb, "budgetUtilizationRatio", h.budgetUtilizationRatio, true);
            AppendJsonProp(sb, "exceedsTierThermalEnvelope", h.exceedsTierThermalEnvelope, true);
            AppendJsonProp(sb, "totalRenderTexturesAudited", h.totalRenderTexturesAudited, true);
            AppendJsonProp(sb, "unresolvedMsaaCount", h.unresolvedMsaaCount, true);
            AppendJsonProp(sb, "persistentDepthStoreCount", h.persistentDepthStoreCount, true);
            AppendJsonProp(sb, "heavy64BitHdrCount", h.heavy64BitHdrCount, true);
            AppendJsonProp(sb, "renderTextureBandwidthGbpsAt60Fps", h.renderTextureBandwidthGbpsAt60Fps, false);
            sb.Append('}');
        }

        private static void AppendPipelineEstimateJson(StringBuilder sb, UrpPipelineBandwidthEstimate est)
        {
            sb.Append('{');
            AppendJsonProp(sb, "width", est.width, true);
            AppendJsonProp(sb, "height", est.height, true);
            AppendJsonProp(sb, "msaaSamples", est.msaaSamples, true);
            AppendJsonProp(sb, "totalMegabytesPerFrame", est.totalMegabytesPerFrame, true);
            AppendJsonProp(sb, "bandwidthGbpsAt60Fps", est.bandwidthGbpsAt60Fps, true);
            AppendJsonProp(sb, "bandwidthGbpsAt120Fps", est.bandwidthGbpsAt120Fps, true);
            AppendJsonProp(sb, "estimatedDramPowerMilliwattsAt60Fps", est.estimatedDramPowerMilliwattsAt60Fps, true);
            AppendJsonProp(sb, "exceedsTierThermalEnvelope", est.exceedsTierThermalEnvelope, true);
            sb.Append("\"passBreakdown\":[");
            if (est.passBreakdown != null)
            {
                for (int i = 0; i < est.passBreakdown.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(est.passBreakdown[i])).Append('"');
                }
            }
            sb.Append("],\"optimizationAdvice\":[");
            if (est.optimizationAdvice != null)
            {
                for (int i = 0; i < est.optimizationAdvice.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(est.optimizationAdvice[i])).Append('"');
                }
            }
            sb.Append("]}");
        }

        private static void AppendRtReportJson(StringBuilder sb, TbdrAuditReport r)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalRenderTexturesAudited", r.totalRenderTexturesAudited, true);
            AppendJsonProp(sb, "estimatedTotalBandwidthGbpsAt60Fps", r.estimatedTotalBandwidthGbpsAt60Fps, true);
            AppendJsonProp(sb, "estimatedTotalBandwidthGbpsAt120Fps", r.estimatedTotalBandwidthGbpsAt120Fps, true);
            AppendJsonProp(sb, "estimatedDramPowerMilliwattsAt60Fps", r.estimatedDramPowerMilliwattsAt60Fps, true);
            sb.Append("\"findings\":[");
            if (r.findings != null)
            {
                for (int i = 0; i < r.findings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RenderTextureBandwidthFinding f = r.findings[i];
                    sb.Append('{');
                    AppendJsonProp(sb, "textureName", f.textureName, true);
                    AppendJsonProp(sb, "width", f.width, true);
                    AppendJsonProp(sb, "height", f.height, true);
                    AppendJsonProp(sb, "antiAliasing", f.antiAliasing, true);
                    AppendJsonProp(sb, "colorFormat", f.colorFormat.ToString(), true);
                    AppendJsonProp(sb, "depthStencilFormat", f.depthStencilFormat.ToString(), true);
                    AppendJsonProp(sb, "estimatedTileStoreMegabytesPerFrame", f.estimatedTileStoreMegabytesPerFrame, true);
                    AppendJsonProp(sb, "estimatedDramPowerMilliwattsAt60Fps", f.estimatedDramPowerMilliwattsAt60Fps, true);
                    AppendJsonProp(sb, "hasUnresolvedMultisamplePenalty", f.hasUnresolvedMultisamplePenalty, true);
                    AppendJsonProp(sb, "hasDepthStorePenalty", f.hasDepthStorePenalty, true);
                    AppendJsonProp(sb, "recommendation", f.recommendation, false);
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private static void AppendTelemetrySummaryJson(StringBuilder sb, TelemetrySummary s)
        {
            sb.Append('{');
            AppendJsonProp(sb, "sampleCount", s.sampleCount, true);
            AppendJsonProp(sb, "avgFps", s.avgFps, true);
            AppendJsonProp(sb, "onePercentLowFps", s.onePercentLowFps, true);
            AppendJsonProp(sb, "pointOnePercentLowFps", s.pointOnePercentLowFps, true);
            AppendJsonProp(sb, "p50FrameMs", s.p50FrameMs, true);
            AppendJsonProp(sb, "p95FrameMs", s.p95FrameMs, true);
            AppendJsonProp(sb, "p99FrameMs", s.p99FrameMs, true);
            AppendJsonProp(sb, "frameTimeJitterStdDevMs", s.frameTimeJitterStdDevMs, true);
            AppendJsonProp(sb, "thermalDriftSlopeMsPerSec", s.thermalDriftSlopeMsPerSec, true);
            AppendJsonProp(sb, "thermalState", s.thermalState.ToString(), true);
            AppendJsonProp(sb, "dominantBottleneck", s.dominantBottleneck.ToString(), true);
            AppendJsonProp(sb, "vsyncMissFrames", s.vsyncMissFrames, true);
            AppendJsonProp(sb, "longestVSyncDropStreak", s.longestVSyncDropStreak, true);
            AppendJsonProp(sb, "gcAllocationSpikeFrames", s.gcAllocationSpikeFrames, false);
            sb.Append('}');
        }

        private static string BuildJsonRpcToolCallResult(string idRaw, string structuredJsonPayload)
        {
            var sb = new StringBuilder(structuredJsonPayload.Length + 256);
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":\"")
              .Append(EscapeJson(structuredJsonPayload))
              .Append("\"}],\"structuredContent\":")
              .Append(structuredJsonPayload)
              .Append(",\"isError\":false}");
            return BuildJsonRpcResult(idRaw, sb.ToString());
        }

        private static string BuildJsonRpcResult(string idRaw, string resultJson)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";
        }

        private static string BuildJsonRpcError(string idRaw, int code, string message)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{code},\"message\":\"{EscapeJson(message)}\"}}}}";
        }

        private static void AppendJsonProp(StringBuilder sb, string key, string val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(val ?? string.Empty)).Append('"');
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, int val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val);
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, float val, bool trailingComma)
        {
            float safe = (float.IsNaN(val) || float.IsInfinity(val)) ? 0f : val;
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(safe.ToString("F3", CultureInfo.InvariantCulture));
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, bool val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val ? "true" : "false");
            if (trailingComma) sb.Append(',');
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string ExtractJsonRpcIdRaw(string json)
        {
            var m = Regex.Match(json, @"""id""\s*:\s*(""[^""\\]*(?:\\.[^""\\]*)*""|-?\d+|null)");
            return m.Success ? m.Groups[1].Value : "null";
        }

        internal static string ExtractJsonStringField(string json, string fieldName)
        {
            string pattern = $@"""{Regex.Escape(fieldName)}""\s*:\s*""((?:[^""\\]|\\.)*)""";
            Match m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static int ExtractJsonIntField(string json, string fieldName, int defaultValue)
        {
            string pattern = $@"""{Regex.Escape(fieldName)}""\s*:\s*(-?\d+)";
            Match m = Regex.Match(json, pattern);
            return (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                ? val
                : defaultValue;
        }

        internal static bool ExtractJsonBoolField(string json, string fieldName, bool defaultValue)
        {
            string pattern = $@"""{Regex.Escape(fieldName)}""\s*:\s*(true|false)";
            Match m = Regex.Match(json, pattern);
            return m.Success ? string.Equals(m.Groups[1].Value, "true", StringComparison.Ordinal) : defaultValue;
        }
    }
}

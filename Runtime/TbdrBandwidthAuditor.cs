using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace YusufOkuducu.MobileThermalProfiler
{
    public enum MobileSoCTier : byte
    {
        EntryLevelBudget = 0,     // Safe sustained DRAM budget: ~2.5 GB/s
        MidRangeMainstream = 1,   // Safe sustained DRAM budget: ~4.5 GB/s
        FlagshipHighEnd = 2       // Safe sustained DRAM budget: ~7.5 GB/s
    }

    [Serializable]
    public struct RenderTextureBandwidthFinding
    {
        public string textureName;
        public int width;
        public int height;
        public int antiAliasing;
        public GraphicsFormat colorFormat;
        public GraphicsFormat depthStencilFormat;
        public float estimatedTileStoreMegabytesPerFrame;
        public float estimatedDramPowerMilliwattsAt60Fps;
        public bool hasUnresolvedMultisamplePenalty;
        public bool hasDepthStorePenalty;
        public string recommendation;
    }

    [Serializable]
    public struct UrpPipelineBandwidthEstimate
    {
        public int width;
        public int height;
        public int msaaSamples;
        public float totalMegabytesPerFrame;
        public float bandwidthGbpsAt60Fps;
        public float bandwidthGbpsAt120Fps;
        public float estimatedDramPowerMilliwattsAt60Fps;
        public bool exceedsTierThermalEnvelope;
        public List<string> passBreakdown;
        public List<string> optimizationAdvice;
    }

    [Serializable]
    public sealed class TbdrAuditReport
    {
        public int totalRenderTexturesAudited;
        public float estimatedTotalBandwidthGbpsAt60Fps;
        public float estimatedTotalBandwidthGbpsAt120Fps;
        public float estimatedDramPowerMilliwattsAt60Fps;
        public float estimatedDramPowerMilliwattsAt120Fps;
        public List<RenderTextureBandwidthFinding> findings = new List<RenderTextureBandwidthFinding>();
    }

    /// <summary>
    /// Audits RenderTextures and URP camera configurations for Tile-Based Deferred Rendering (TBDR)
    /// memory bandwidth hazards and external LPDDR DRAM power draw on mobile GPUs (ARM Mali, Qualcomm Adreno, Apple GPU).
    /// High external DRAM read/write traffic (~105 pJ/byte) is the primary driver of mobile battery drain and thermal throttling.
    /// </summary>
    public static class TbdrBandwidthAuditor
    {
        // Industry-average external LPDDR4X/LPDDR5 energy cost: ~105 picojoules per byte (~107.5 mW per GB/s)
        private const float DramMilliwattsPerGbps = 107.5f;

        public static TbdrAuditReport AuditActiveRenderTextures()
        {
            RenderTexture[] textures = Resources.FindObjectsOfTypeAll<RenderTexture>();
            return AuditRenderTextures(textures);
        }

        public static TbdrAuditReport AuditRenderTextures(IReadOnlyList<RenderTexture> textures)
        {
            var report = new TbdrAuditReport();
            if (textures == null) return report;

            float totalMbPerFrame = 0f;

            for (int i = 0; i < textures.Count; i++)
            {
                RenderTexture rt = textures[i];
                if (rt == null || rt.width <= 0 || rt.height <= 0)
                    continue;

                report.totalRenderTexturesAudited++;

                var finding = AnalyzeRenderTexture(
                    rt.name,
                    rt.width,
                    rt.height,
                    Mathf.Max(1, rt.antiAliasing),
                    rt.graphicsFormat,
                    rt.depthStencilFormat,
                    rt.bindTextureMS,
                    rt.useMipMap && rt.autoGenerateMips);

                totalMbPerFrame += finding.estimatedTileStoreMegabytesPerFrame;
                report.findings.Add(finding);
            }

            report.findings.Sort((a, b) =>
                b.estimatedTileStoreMegabytesPerFrame.CompareTo(a.estimatedTileStoreMegabytesPerFrame));

            report.estimatedTotalBandwidthGbpsAt60Fps = (totalMbPerFrame * 60f) / 1024f;
            report.estimatedTotalBandwidthGbpsAt120Fps = (totalMbPerFrame * 120f) / 1024f;
            report.estimatedDramPowerMilliwattsAt60Fps = report.estimatedTotalBandwidthGbpsAt60Fps * DramMilliwattsPerGbps;
            report.estimatedDramPowerMilliwattsAt120Fps = report.estimatedTotalBandwidthGbpsAt120Fps * DramMilliwattsPerGbps;

            return report;
        }

        public static RenderTextureBandwidthFinding AnalyzeRenderTexture(
            string name,
            int width,
            int height,
            int msaaSamples,
            GraphicsFormat colorFormat,
            GraphicsFormat depthFormat,
            bool bindMultisampled,
            bool autoGenMips)
        {
            int safeW = Math.Max(0, width);
            int safeH = Math.Max(0, height);
            int safeMsaa = Math.Max(1, msaaSamples);
            long pixels = (long)safeW * safeH;

            int colorBytesPerPixel = GetBytesPerPixel(colorFormat);
            int depthBytesPerPixel = GetBytesPerPixel(depthFormat);

            // On TBDR GPUs, resolved color write costs 1x colorBytesPerPixel.
            // However, if bindTextureMS is true or MSAA isn't resolved in tile memory, it flushes all samples to DRAM!
            int effectiveColorMultiplier = (bindMultisampled && safeMsaa > 1) ? safeMsaa : 1;
            long colorWriteBytes = pixels * colorBytesPerPixel * effectiveColorMultiplier;

            // Depth buffer stored back to system RAM instead of Memoryless/Discard costs full depth write + read
            bool hasDepth = depthBytesPerPixel > 0 && pixels > 0;
            long depthTrafficBytes = hasDepth ? pixels * depthBytesPerPixel : 0;

            // Mipmap generation forces an extra full read + 1/3 write pass in external DRAM
            long mipTrafficBytes = (autoGenMips && pixels > 0) ? (pixels * colorBytesPerPixel * 4) / 3 : 0;

            float totalMegabytes = (colorWriteBytes + depthTrafficBytes + mipTrafficBytes) / (1024f * 1024f);
            float gbpsAt60 = (totalMegabytes * 60f) / 1024f;
            float powerMwAt60 = gbpsAt60 * DramMilliwattsPerGbps;

            string rec = "Optimal tile local configuration.";
            if (bindMultisampled && safeMsaa > 1)
            {
                rec = "CRITICAL: bindTextureMS forces full MSAA sample flush from on-chip tile memory to DRAM. Disable bindTextureMS to resolve on-chip.";
            }
            else if (colorBytesPerPixel >= 8)
            {
                rec = "HIGH BANDWIDTH: 64-bit+ HDR format detected. Use B10G11R11_UFloatPack32 (R11G11B10) on Mobile to cut color bandwidth by 50%.";
            }
            else if (hasDepth && safeW >= 1280)
            {
                rec = "DEPTH STORE HAZARD: Large RenderTexture allocates a persistent Depth/Stencil buffer. Mark depth as Memoryless or use RenderBufferStoreAction.DontCare.";
            }
            else if (autoGenMips)
            {
                rec = "MIPMAP BANDWIDTH: autoGenerateMips causes extra DRAM read/write passes every frame.";
            }

            return new RenderTextureBandwidthFinding
            {
                textureName = string.IsNullOrEmpty(name) ? "<Unnamed RT>" : name,
                width = safeW,
                height = safeH,
                antiAliasing = safeMsaa,
                colorFormat = colorFormat,
                depthStencilFormat = depthFormat,
                estimatedTileStoreMegabytesPerFrame = totalMegabytes,
                estimatedDramPowerMilliwattsAt60Fps = powerMwAt60,
                hasUnresolvedMultisamplePenalty = bindMultisampled && safeMsaa > 1,
                hasDepthStorePenalty = hasDepth,
                recommendation = rec
            };
        }

        /// <summary>
        /// Simulates full-frame URP mobile camera pass bandwidth (Color resolve, Depth copy, Opaque texture copy,
        /// Shadowmap atlas, and Post-Processing Bloom pyramid) and validates against the target SoC thermal envelope.
        /// </summary>
        public static UrpPipelineBandwidthEstimate EstimateUrpCameraPipelineBandwidth(
            int width,
            int height,
            int msaaSamples,
            GraphicsFormat hdrOrLdrColorFormat,
            bool cameraDepthTextureEnabled,
            bool cameraOpaqueTextureEnabled,
            bool postProcessingBloomEnabled,
            int shadowmapResolution = 2048,
            MobileSoCTier socTier = MobileSoCTier.MidRangeMainstream)
        {
            int safeW = Math.Max(0, width);
            int safeH = Math.Max(0, height);
            int safeMsaa = Math.Max(1, msaaSamples);
            long pixels = (long)safeW * safeH;
            int colorBpp = Math.Max(4, GetBytesPerPixel(hdrOrLdrColorFormat));

            var breakdown = new List<string>(6);
            var advice = new List<string>(4);
            float totalMb = 0f;

            // 1. Base Color Tile Store + Final Blit/Compositing Read
            float baseColorMb = (pixels * colorBpp * 2f) / (1024f * 1024f);
            totalMb += baseColorMb;
            breakdown.Add($"Main Camera Color Write + Display Read ({hdrOrLdrColorFormat}): {baseColorMb:F2} MB/frame");

            if (colorBpp >= 8)
            {
                advice.Add("Switch URP HDR Color Buffer format from 64-bit R16G16B16A16_SFloat to 32-bit B10G11R11_UFloatPack32 to halve color bandwidth.");
            }

            // 2. _CameraDepthTexture (Breaks single-pass tile residency or forces Depth resolve + read)
            if (cameraDepthTextureEnabled)
            {
                int depthMultiplier = safeMsaa > 1 ? 3 : 2; // Depth prepass/resolve write + shader sample read
                float depthCopyMb = (pixels * 4f * depthMultiplier) / (1024f * 1024f);
                totalMb += depthCopyMb;
                breakdown.Add($"_CameraDepthTexture Resolve & Sample: {depthCopyMb:F2} MB/frame");
                advice.Add("Disable Require Depth Texture on mobile URP cameras or use framebuffer fetch (subpass input) to keep depth in on-chip tile SRAM.");
            }

            // 3. _CameraOpaqueTexture (Forces mid-frame tile flush, downsample copy, and reload!)
            if (cameraOpaqueTextureEnabled)
            {
                float opaqueCopyMb = (pixels * colorBpp * 2.25f) / (1024f * 1024f);
                totalMb += opaqueCopyMb;
                breakdown.Add($"_CameraOpaqueTexture Mid-Frame Tile Flush & Copy: {opaqueCopyMb:F2} MB/frame");
                advice.Add("Avoid _CameraOpaqueTexture on TBDR GPUs; it splits the render pass and forces a full tile flush + reload from external DRAM.");
            }

            // 4. Shadowmap Atlas Write + PCF Read
            if (shadowmapResolution > 0)
            {
                long shadowPixels = (long)shadowmapResolution * shadowmapResolution;
                float shadowMb = (shadowPixels * 2f * 2f) / (1024f * 1024f); // 16-bit depth write + PCF read
                totalMb += shadowMb;
                breakdown.Add($"Shadowmap Atlas ({shadowmapResolution}x{shadowmapResolution}): {shadowMb:F2} MB/frame");
            }

            // 5. Post-Processing Bloom Pyramid (Downsample + Upsample mip chain ~0.66x screen traffic)
            if (postProcessingBloomEnabled)
            {
                float bloomMb = (pixels * colorBpp * 1.33f) / (1024f * 1024f);
                totalMb += bloomMb;
                breakdown.Add($"URP Bloom Downsample/Upsample Pyramid: {bloomMb:F2} MB/frame");
            }

            float gbps60 = (totalMb * 60f) / 1024f;
            float gbps120 = (totalMb * 120f) / 1024f;
            float tierLimitGbps = GetSustainedBandwidthBudgetGbps(socTier);

            return new UrpPipelineBandwidthEstimate
            {
                width = safeW,
                height = safeH,
                msaaSamples = safeMsaa,
                totalMegabytesPerFrame = totalMb,
                bandwidthGbpsAt60Fps = gbps60,
                bandwidthGbpsAt120Fps = gbps120,
                estimatedDramPowerMilliwattsAt60Fps = gbps60 * DramMilliwattsPerGbps,
                exceedsTierThermalEnvelope = gbps60 > tierLimitGbps,
                passBreakdown = breakdown,
                optimizationAdvice = advice
            };
        }

        public static float GetSustainedBandwidthBudgetGbps(MobileSoCTier tier)
        {
            switch (tier)
            {
                case MobileSoCTier.EntryLevelBudget:
                    return 2.5f;
                case MobileSoCTier.FlagshipHighEnd:
                    return 7.5f;
                default:
                    return 4.5f;
            }
        }

        public static int GetBytesPerPixel(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.None:
                    return 0;
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    return 1;
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.D16_UNorm:
                    return 2;
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.B10G11R11_UFloatPack32:
                case GraphicsFormat.A2B10G10R10_UNormPack32:
                case GraphicsFormat.R16G16_SFloat:
                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.D24_UNorm:
                case GraphicsFormat.D24_UNorm_S8_UInt:
                case GraphicsFormat.D32_SFloat:
                    return 4;
                case GraphicsFormat.D32_SFloat_S8_UInt:
                    return 5;
                case GraphicsFormat.R16G16B16A16_SFloat:
                case GraphicsFormat.R16G16B16A16_UNorm:
                case GraphicsFormat.R32G32_SFloat:
                    return 8;
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return 16;
                default:
                    return 4;
            }
        }

        public static string ExportTbdrAuditToMarkdown(TbdrAuditReport report, UrpPipelineBandwidthEstimate? pipelineEstimate = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Mobile TBDR Bandwidth & LPDDR Power Audit Report");
            sb.AppendLine();

            if (report != null)
            {
                sb.AppendLine("## 1. Allocated RenderTextures Audit");
                sb.AppendLine($"- **RenderTextures Audited**: {report.totalRenderTexturesAudited}");
                sb.AppendLine($"- **Est. DRAM Traffic @ 60 FPS**: {report.estimatedTotalBandwidthGbpsAt60Fps.ToString("F2", CultureInfo.InvariantCulture)} GB/s (~{report.estimatedDramPowerMilliwattsAt60Fps.ToString("F0", CultureInfo.InvariantCulture)} mW)");
                sb.AppendLine($"- **Est. DRAM Traffic @ 120 FPS**: {report.estimatedTotalBandwidthGbpsAt120Fps.ToString("F2", CultureInfo.InvariantCulture)} GB/s (~{report.estimatedDramPowerMilliwattsAt120Fps.ToString("F0", CultureInfo.InvariantCulture)} mW)");
                sb.AppendLine();

                if (report.findings != null && report.findings.Count > 0)
                {
                    sb.AppendLine("| RenderTexture | Resolution | MSAA | Color Format | MB/Frame | Power @ 60FPS | Recommendation |");
                    sb.AppendLine("|---|---|---:|---|---:|---:|---|");
                    for (int i = 0; i < report.findings.Count; i++)
                    {
                        var f = report.findings[i];
                        sb.Append("| `").Append(f.textureName).Append("` | ")
                          .Append(f.width).Append('x').Append(f.height).Append(" | ")
                          .Append(f.antiAliasing).Append("x | `")
                          .Append(f.colorFormat).Append("` | ")
                          .Append(f.estimatedTileStoreMegabytesPerFrame.ToString("F2", CultureInfo.InvariantCulture)).Append(" MB | ")
                          .Append(f.estimatedDramPowerMilliwattsAt60Fps.ToString("F0", CultureInfo.InvariantCulture)).Append(" mW | ")
                          .Append(f.recommendation).AppendLine(" |");
                    }
                    sb.AppendLine();
                }
            }

            if (pipelineEstimate.HasValue)
            {
                UrpPipelineBandwidthEstimate est = pipelineEstimate.Value;
                sb.AppendLine("## 2. URP Mobile Camera Pipeline Simulation");
                sb.AppendLine($"- **Resolution**: {est.width}x{est.height} (MSAA {est.msaaSamples}x)");
                sb.AppendLine($"- **Total Traffic**: {est.totalMegabytesPerFrame.ToString("F2", CultureInfo.InvariantCulture)} MB/frame ({est.bandwidthGbpsAt60Fps.ToString("F2", CultureInfo.InvariantCulture)} GB/s @ 60 FPS | ~{est.estimatedDramPowerMilliwattsAt60Fps.ToString("F0", CultureInfo.InvariantCulture)} mW)");
                sb.AppendLine($"- **Exceeds Tier Envelope**: `{est.exceedsTierThermalEnvelope}`");
                if (est.passBreakdown != null)
                {
                    for (int i = 0; i < est.passBreakdown.Count; i++)
                        sb.AppendLine($"  - {est.passBreakdown[i]}");
                }
            }

            return sb.ToString();
        }
    }
}

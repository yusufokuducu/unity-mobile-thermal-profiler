using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace YusufOkuducu.MobileThermalProfiler
{
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
        public bool hasUnresolvedMultisamplePenalty;
        public bool hasDepthStorePenalty;
        public string recommendation;
    }

    [Serializable]
    public sealed class TbdrAuditReport
    {
        public int totalRenderTexturesAudited;
        public float estimatedTotalBandwidthGbpsAt60Fps;
        public float estimatedTotalBandwidthGbpsAt120Fps;
        public List<RenderTextureBandwidthFinding> findings = new List<RenderTextureBandwidthFinding>();
    }

    /// <summary>
    /// Audits RenderTextures and camera targets for Tile-Based Deferred Rendering (TBDR)
    /// memory bandwidth hazards on mobile GPUs (ARM Mali, Qualcomm Adreno, Apple GPU).
    /// High external DRAM read/write traffic is the #1 cause of mobile battery drain and thermal throttling.
    /// </summary>
    public static class TbdrBandwidthAuditor
    {
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

            // Convert MB/frame -> GB/sec at 60 FPS and 120 FPS
            report.estimatedTotalBandwidthGbpsAt60Fps = (totalMbPerFrame * 60f) / 1024f;
            report.estimatedTotalBandwidthGbpsAt120Fps = (totalMbPerFrame * 120f) / 1024f;

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
            long pixels = (long)width * height;
            int colorBytesPerPixel = GetBytesPerPixel(colorFormat);
            int depthBytesPerPixel = GetBytesPerPixel(depthFormat);

            // On TBDR GPUs, resolved color write costs 1x colorBytesPerPixel.
            // However, if bindTextureMS is true or MSAA isn't resolved in tile memory, it flushes all samples!
            int effectiveColorMultiplier = bindMultisampled ? Mathf.Max(1, msaaSamples) : 1;
            long colorWriteBytes = pixels * colorBytesPerPixel * effectiveColorMultiplier;

            // Depth buffer stored back to system RAM instead of Memoryless/Discard costs full depth write + read
            bool hasDepth = depthBytesPerPixel > 0;
            long depthTrafficBytes = hasDepth ? pixels * depthBytesPerPixel : 0;

            // Mipmap generation forces an extra full read + 1/3 write pass in external DRAM
            long mipTrafficBytes = autoGenMips ? (pixels * colorBytesPerPixel * 4) / 3 : 0;

            float totalMegabytes = (colorWriteBytes + depthTrafficBytes + mipTrafficBytes) / (1024f * 1024f);

            string rec = "Optimal tile local configuration.";
            if (bindMultisampled && msaaSamples > 1)
            {
                rec = "CRITICAL: bindTextureMS forces full MSAA sample flush from on-chip tile memory to DRAM. Disable bindTextureMS to resolve on-chip.";
            }
            else if (colorBytesPerPixel >= 8)
            {
                rec = "HIGH BANDWIDTH: 64-bit+ HDR format detected. Use B10G11R11_UFloatPack32 (R11G11B10) on Mobile to cut color bandwidth by 50%.";
            }
            else if (hasDepth && width >= 1280)
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
                width = width,
                height = height,
                antiAliasing = msaaSamples,
                colorFormat = colorFormat,
                depthStencilFormat = depthFormat,
                estimatedTileStoreMegabytesPerFrame = totalMegabytes,
                hasUnresolvedMultisamplePenalty = bindMultisampled && msaaSamples > 1,
                hasDepthStorePenalty = hasDepth,
                recommendation = rec
            };
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
    }
}

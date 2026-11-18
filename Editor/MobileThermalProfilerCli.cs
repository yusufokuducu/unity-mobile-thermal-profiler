using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace YusufOkuducu.MobileThermalProfiler.Editor
{
    public static class MobileThermalProfilerCli
    {
        /// <summary>
        /// Batchmode / CI entrypoint:
        /// unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileThermalProfiler.Editor.MobileThermalProfilerCli.RunCiAudit
        /// </summary>
        public static void RunCiAudit()
        {
            TbdrAuditReport rtReport = TbdrBandwidthAuditor.AuditActiveRenderTextures();
            UrpPipelineBandwidthEstimate defaultPipeline = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                width: 2400,
                height: 1080,
                msaaSamples: 4,
                hdrOrLdrColorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
                cameraDepthTextureEnabled: false,
                cameraOpaqueTextureEnabled: false,
                postProcessingBloomEnabled: true,
                shadowmapResolution: 2048,
                socTier: MobileSoCTier.MidRangeMainstream);

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string mdPath = Path.Combine(logDir, "MobileTbdrBandwidthAudit.md");
            string markdown = TbdrBandwidthAuditor.ExportTbdrAuditToMarkdown(rtReport, defaultPipeline);
            File.WriteAllText(mdPath, markdown, Encoding.UTF8);

            Debug.Log(
                $"[MobileThermalProfiler CI] Audited {rtReport.totalRenderTexturesAudited} RenderTextures " +
                $"({rtReport.estimatedTotalBandwidthGbpsAt60Fps:F2} GB/s @ 60 FPS). Report saved to {mdPath}");
        }
    }
}

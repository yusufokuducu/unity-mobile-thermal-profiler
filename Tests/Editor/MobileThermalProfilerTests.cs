using NUnit.Framework;
using UnityEngine.Experimental.Rendering;

namespace YusufOkuducu.MobileThermalProfiler.Tests
{
    public sealed class MobileThermalProfilerTests
    {
        [Test]
        public void TelemetrySummary_CalculatesPercentilesAndJitterCorrectly()
        {
            var telemetry = new MobileThermalTelemetry(100);

            // Push 99 smooth frames at 16.0 ms and 1 spike frame at 50.0 ms
            for (int i = 0; i < 99; i++)
            {
                telemetry.PushSample(new FrameTelemetrySample
                {
                    frameIndex = i,
                    unscaledDeltaTimeMs = 16.0f,
                    cpuMainThreadMs = 8.0f,
                    cpuRenderThreadMs = 4.0f,
                    gpuFrameTimeMs = 10.0f,
                    thermalHeadroomNormalized = 0.2f,
                    bottleneck = BottleneckKind.Balanced
                });
            }

            telemetry.PushSample(new FrameTelemetrySample
            {
                frameIndex = 99,
                unscaledDeltaTimeMs = 50.0f,
                cpuMainThreadMs = 12.0f,
                cpuRenderThreadMs = 6.0f,
                gpuFrameTimeMs = 48.0f,
                thermalHeadroomNormalized = 0.92f,
                bottleneck = BottleneckKind.ThermalThrottled
            });

            TelemetrySummary summary = telemetry.ComputeSummary(60f);

            Assert.AreEqual(100, summary.sampleCount);
            Assert.AreEqual(16.0f, summary.p99FrameMs, 0.01f);
            Assert.AreEqual(50.0f, summary.p99_9FrameMs, 0.01f);
            Assert.AreEqual(1, summary.thermalThrottleFrames);
            Assert.AreEqual(1, summary.vsyncMissFrames);
            Assert.Greater(summary.frameTimeJitterStdDevMs, 3.0f);
        }

        [Test]
        public void TbdrAuditor_DetectsUnresolvedMultisampleAndHdrBandwidthPenalties()
        {
            var hdrMsaaFinding = TbdrBandwidthAuditor.AnalyzeRenderTexture(
                "CameraColorHDR_MSAA4x",
                2400,
                1080,
                4,
                GraphicsFormat.R16G16B16A16_SFloat,
                GraphicsFormat.D24_UNorm_S8_UInt,
                bindMultisampled: true,
                autoGenMips: false);

            var mobileOptFinding = TbdrBandwidthAuditor.AnalyzeRenderTexture(
                "CameraColorB10G11R11_Resolved",
                2400,
                1080,
                4,
                GraphicsFormat.B10G11R11_UFloatPack32,
                GraphicsFormat.None,
                bindMultisampled: false,
                autoGenMips: false);

            Assert.IsTrue(hdrMsaaFinding.hasUnresolvedMultisamplePenalty);
            Assert.IsTrue(hdrMsaaFinding.hasDepthStorePenalty);
            Assert.Greater(hdrMsaaFinding.estimatedTileStoreMegabytesPerFrame, mobileOptFinding.estimatedTileStoreMegabytesPerFrame * 6f);
        }
    }
}

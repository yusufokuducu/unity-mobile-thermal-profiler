using System;
using NUnit.Framework;
using UnityEngine.Experimental.Rendering;

namespace YusufOkuducu.MobileThermalProfiler.Tests
{
    public sealed class MobileThermalProfilerTests
    {
        [Test]
        public void TelemetrySummary_CalculatesPercentilesJitterStreaksAndGcSpikesCorrectly()
        {
            var telemetry = new MobileThermalTelemetry(100);

            // Push 95 smooth frames at 16.0 ms, 4 consecutive VSync-miss frames at 22.0 ms, and 1 severe thermal spike at 50.0 ms
            long monoBase = 10 * 1024 * 1024;
            for (int i = 0; i < 95; i++)
            {
                telemetry.PushSample(new FrameTelemetrySample
                {
                    frameIndex = i,
                    unscaledDeltaTimeMs = 16.0f,
                    cpuMainThreadMs = 8.0f,
                    cpuRenderThreadMs = 4.0f,
                    gpuFrameTimeMs = 10.0f,
                    thermalHeadroomNormalized = 0.2f,
                    monoHeapBytes = monoBase + (i == 10 ? 64 * 1024 : 0), // 64KB spike on frame 10
                    bottleneck = BottleneckKind.Balanced
                });
            }

            for (int i = 95; i < 99; i++)
            {
                telemetry.PushSample(new FrameTelemetrySample
                {
                    frameIndex = i,
                    unscaledDeltaTimeMs = 22.0f,
                    cpuMainThreadMs = 18.0f,
                    cpuRenderThreadMs = 5.0f,
                    gpuFrameTimeMs = 14.0f,
                    thermalHeadroomNormalized = 0.6f,
                    monoHeapBytes = monoBase,
                    bottleneck = BottleneckKind.CpuMainThread
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
                monoHeapBytes = monoBase,
                bottleneck = BottleneckKind.ThermalThrottled
            });

            TelemetrySummary summary = telemetry.ComputeSummary(60f);

            Assert.AreEqual(100, summary.sampleCount);
            Assert.AreEqual(16.0f, summary.p50FrameMs, 0.01f);
            Assert.AreEqual(16.0f, summary.p95FrameMs, 0.01f);
            Assert.AreEqual(22.0f, summary.p99FrameMs, 0.01f);
            Assert.AreEqual(50.0f, summary.p99_9FrameMs, 0.01f);
            Assert.AreEqual(1, summary.thermalThrottleFrames);
            Assert.AreEqual(5, summary.vsyncMissFrames);
            Assert.AreEqual(5, summary.longestVSyncDropStreak);
            Assert.AreEqual(1, summary.gcAllocationSpikeFrames);
            Assert.Greater(summary.frameTimeJitterStdDevMs, 3.0f);
            Assert.AreEqual(BottleneckKind.Balanced, summary.dominantBottleneck);
        }

        [Test]
        public void TelemetrySummary_CalculatesOlsThermalDriftSlopeAndRingBufferWrapAround()
        {
            var telemetry = new MobileThermalTelemetry(50);

            // Push 80 frames into a 50-capacity ring buffer with steadily increasing GPU frame time (+1ms per second)
            for (int i = 0; i < 80; i++)
            {
                telemetry.PushSample(new FrameTelemetrySample
                {
                    frameIndex = i,
                    unscaledDeltaTimeMs = 20.0f, // 0.02s per frame -> 50 frames = 1.0s
                    cpuMainThreadMs = 8.0f,
                    cpuRenderThreadMs = 3.0f,
                    gpuFrameTimeMs = 10.0f + (i * 0.02f), // +1.0 ms per second slope
                    thermalHeadroomNormalized = 0.65f,
                    bottleneck = BottleneckKind.GpuFragmentOrBandwidth
                });
            }

            Assert.AreEqual(50, telemetry.Count);
            Assert.AreEqual(30, telemetry.GetSampleChronological(0).frameIndex);
            Assert.AreEqual(79, telemetry.GetSampleChronological(49).frameIndex);

            TelemetrySummary summary = telemetry.ComputeSummary(60f);
            Assert.AreEqual(1.0f, summary.thermalDriftSlopeMsPerSec, 0.05f);
            Assert.AreEqual(MobileThermalState.Serious, summary.thermalState);
        }

        [Test]
        public void Telemetry_HandlesEmptyBufferNegativeValuesAndOutOfBoundsGracefully()
        {
            var telemetry = new MobileThermalTelemetry(10); // Clamped to min 30
            Assert.AreEqual(30, telemetry.Capacity);

            TelemetrySummary empty = telemetry.ComputeSummary(60f);
            Assert.AreEqual(0, empty.sampleCount);
            Assert.AreEqual(0f, empty.avgFps);

            Assert.Throws<ArgumentOutOfRangeException>(() => telemetry.GetSampleChronological(0));

            telemetry.PushSample(new FrameTelemetrySample
            {
                frameIndex = 1,
                unscaledDeltaTimeMs = float.NaN,
                cpuMainThreadMs = -5f,
                gpuFrameTimeMs = float.PositiveInfinity
            });

            FrameTelemetrySample sanitized = telemetry.GetSampleChronological(0);
            Assert.AreEqual(0f, sanitized.unscaledDeltaTimeMs);
            Assert.AreEqual(0f, sanitized.cpuMainThreadMs);
            Assert.AreEqual(0f, sanitized.gpuFrameTimeMs);

            // Verify Infinity / NaN targetFps does not zero out frame budget or falsely mark smooth frames as VSync misses
            var smoothTelemetry = new MobileThermalTelemetry(30);
            for (int i = 0; i < 10; i++)
            {
                smoothTelemetry.PushSample(new FrameTelemetrySample
                {
                    frameIndex = i,
                    unscaledDeltaTimeMs = 16.0f,
                    cpuMainThreadMs = 6.0f,
                    gpuFrameTimeMs = 8.0f,
                    allocatedMemoryBytes = -1024L,
                    monoHeapBytes = -512L
                });
            }
            Assert.AreEqual(0L, smoothTelemetry.GetSampleChronological(0).allocatedMemoryBytes);
            Assert.AreEqual(0L, smoothTelemetry.GetSampleChronological(0).monoHeapBytes);

            TelemetrySummary infSummary = smoothTelemetry.ComputeSummary(float.PositiveInfinity);
            TelemetrySummary nanSummary = smoothTelemetry.ComputeSummary(float.NaN);
            Assert.AreEqual(0, infSummary.vsyncMissFrames);
            Assert.AreEqual(0, nanSummary.vsyncMissFrames);

            string md = smoothTelemetry.ExportSummaryToMarkdown(float.PositiveInfinity);
            StringAssert.Contains("Target: 60 FPS", md);
            StringAssert.DoesNotContain("Infinity", md);
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
            Assert.Greater(hdrMsaaFinding.estimatedDramPowerMilliwattsAt60Fps, 500f);
        }

        [Test]
        public void UrpPipelineSimulator_FlagsExcessiveDepthAndOpaqueCopiesAgainstSoCTier()
        {
            UrpPipelineBandwidthEstimate heavyPipeline = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                width: 2400,
                height: 1080,
                msaaSamples: 4,
                hdrOrLdrColorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                cameraDepthTextureEnabled: true,
                cameraOpaqueTextureEnabled: true,
                postProcessingBloomEnabled: true,
                shadowmapResolution: 2048,
                socTier: MobileSoCTier.MidRangeMainstream);

            UrpPipelineBandwidthEstimate leanPipeline = TbdrBandwidthAuditor.EstimateUrpCameraPipelineBandwidth(
                width: 1920,
                height: 864,
                msaaSamples: 4,
                hdrOrLdrColorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
                cameraDepthTextureEnabled: false,
                cameraOpaqueTextureEnabled: false,
                postProcessingBloomEnabled: false,
                shadowmapResolution: 1024,
                socTier: MobileSoCTier.MidRangeMainstream);

            Assert.IsTrue(heavyPipeline.exceedsTierThermalEnvelope);
            Assert.IsFalse(leanPipeline.exceedsTierThermalEnvelope);
            Assert.Greater(heavyPipeline.bandwidthGbpsAt60Fps, leanPipeline.bandwidthGbpsAt60Fps * 3f);
            Assert.GreaterOrEqual(heavyPipeline.optimizationAdvice.Count, 3);
        }
    }
}

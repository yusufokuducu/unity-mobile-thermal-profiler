using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using YusufOkuducu.MobileThermalProfiler.Editor;

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

        [Test]
        public void McpBridge_AutoFixRenderTextures_And_AutoTuneUrpPipeline_EliminateBandwidthHazards()
        {
            var heavyRt = new RenderTexture
            {
                name = "HeavyCameraTarget",
                width = 2400,
                height = 1080,
                antiAliasing = 4,
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt,
                bindTextureMS = true,
                useMipMap = true,
                autoGenerateMips = true
            };

            RenderTextureAutoFixReport rtFix = MobileThermalProfilerMcpBridge.AutoFixRenderTextures(new[] { heavyRt });
            Assert.AreEqual(1, rtFix.totalInspected);
            Assert.AreEqual(1, rtFix.modifiedCount);
            Assert.AreEqual(3, rtFix.appliedFixes.Count);
            Assert.IsFalse(heavyRt.bindTextureMS);
            Assert.AreEqual(GraphicsFormat.B10G11R11_UFloatPack32, heavyRt.graphicsFormat);
            Assert.IsFalse(heavyRt.autoGenerateMips);
            Assert.Less(rtFix.bandwidthGbpsAfter, rtFix.bandwidthGbpsBefore * 0.25f);
            Assert.Greater(rtFix.dramPowerMilliwattsSavedAt60Fps, 400f);

            // Auto-Tune URP Pipeline for MidRangeMainstream (4.5 GB/s budget)
            UrpPipelineAutoTuneReport tuneReport = MobileThermalProfilerMcpBridge.AutoTuneUrpPipelineForSoCTier(
                width: 2400,
                height: 1080,
                msaaSamples: 4,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                cameraDepthTextureEnabled: true,
                cameraOpaqueTextureEnabled: true,
                postProcessingBloomEnabled: true,
                shadowmapResolution: 2048,
                socTier: MobileSoCTier.MidRangeMainstream);

            Assert.IsTrue(tuneReport.beforeEstimate.exceedsTierThermalEnvelope);
            Assert.IsTrue(tuneReport.modified);
            Assert.IsFalse(tuneReport.afterEstimate.exceedsTierThermalEnvelope);
            Assert.AreEqual(GraphicsFormat.B10G11R11_UFloatPack32, tuneReport.tunedColorFormat);
            Assert.IsFalse(tuneReport.tunedOpaqueTextureEnabled);
            Assert.Greater(tuneReport.dramPowerMilliwattsSavedAt60Fps, 300f);
        }

        [Test]
        public void McpBridge_HandlesJsonRpcInitializeToolsListToolsCallAndErrors()
        {
            string initRes = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");
            StringAssert.Contains(@"""protocolVersion"":""2024-11-05""", initRes);
            StringAssert.Contains(@"""name"":""unity-mobile-thermal-profiler""", initRes);

            string listRes = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list""}");
            StringAssert.Contains("thermal_profiler.audit_render_textures", listRes);
            StringAssert.Contains("thermal_profiler.simulate_urp_pipeline", listRes);
            StringAssert.Contains("thermal_profiler.autotune_urp_pipeline", listRes);

            string callSim = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""thermal_profiler.simulate_urp_pipeline"",""arguments"":{""width"":2400,""height"":1080,""msaaSamples"":4,""colorFormat"":""R16G16B16A16_SFloat"",""cameraDepthTextureEnabled"":true,""cameraOpaqueTextureEnabled"":true,""postProcessingBloomEnabled"":true,""shadowmapResolution"":2048,""socTier"":""MidRangeMainstream""}}}");
            StringAssert.Contains(@"""exceedsTierThermalEnvelope"":true", callSim);
            StringAssert.Contains(@"""isError"":false", callSim);

            string callTune = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""thermal_profiler.autotune_urp_pipeline"",""arguments"":{""width"":2400,""height"":1080,""msaaSamples"":4,""colorFormat"":""R16G16B16A16_SFloat"",""cameraDepthTextureEnabled"":true,""cameraOpaqueTextureEnabled"":true,""postProcessingBloomEnabled"":true,""shadowmapResolution"":2048,""socTier"":""MidRangeMainstream""}}}");
            StringAssert.Contains(@"""modified"":true", callTune);
            StringAssert.Contains(@"""exceedsTierThermalEnvelopeAfter"":false", callTune);

            string errUnknown = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":99,""method"":""nonexistent""}");
            StringAssert.Contains("-32601", errUnknown);
        }

        [Test]
        public void UiToolkitWindow_BuildsBandwidthHeatmapFiltersRenderTexturesAndAutoTunesPipeline()
        {
            var win = new ThermalBandwidthProfilerWindow();
            win.CreateGUI();
            Assert.Greater(win.rootVisualElement.childCount, 0);

            var rt1 = new RenderTexture
            {
                name = "ShadowMapAtlas",
                width = 2048,
                height = 2048,
                antiAliasing = 1,
                graphicsFormat = GraphicsFormat.None,
                depthStencilFormat = GraphicsFormat.D16_UNorm
            };
            var rt2 = new RenderTexture
            {
                name = "UnresolvedColorMSAA",
                width = 2400,
                height = 1080,
                antiAliasing = 4,
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                bindTextureMS = true
            };

            TbdrAuditReport customReport = TbdrBandwidthAuditor.AuditRenderTextures(new[] { rt1, rt2 });
            win.SetCustomAuditReport(customReport);
            Assert.AreEqual(2, win.GetFilteredFindings().Count);

            win.SetSearchFilter("UnresolvedColorMSAA");
            Assert.AreEqual(1, win.GetFilteredFindings().Count);
            Assert.AreEqual("UnresolvedColorMSAA", win.GetFilteredFindings()[0].textureName);

            win.ConfigurePipelineSimulator(
                2400, 1080, 4, GraphicsFormat.R16G16B16A16_SFloat,
                depthTex: true, opaqueTex: true, bloom: true, shadowRes: 2048, tier: MobileSoCTier.MidRangeMainstream);
            Assert.IsTrue(win.CurrentPipelineEstimate.exceedsTierThermalEnvelope);

            win.ApplyPipelineAutoTune();
            Assert.IsFalse(win.CurrentPipelineEstimate.exceedsTierThermalEnvelope);

            // Verify JSON-RPC 2.0 handles alphabetical key order ("arguments" before "name", "id" at end) and unicode escapes
            string reorderedJson =
                "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"arguments\":{\"width\":2400,\"height\":1080,\"msaaSamples\":4,\"colorFormat\":\"R16G16B16A16_SFloat\",\"cameraDepthTextureEnabled\":true,\"cameraOpaqueTextureEnabled\":true,\"postProcessingBloomEnabled\":true,\"shadowmapResolution\":2048,\"socTier\":\"EntryLevelBudget\"},\"name\":\"thermal_profiler.autotune_urp_pipeline\"},\"id\":77}";
            string rpcRes = MobileThermalProfilerMcpBridge.HandleJsonRpcRequest(reorderedJson);
            StringAssert.Contains("\"id\":77", rpcRes);
            StringAssert.Contains("\"exceedsTierThermalEnvelopeAfter\":false", rpcRes);

            // Verify parameterless CLI overload exists for Unity -executeMethod
            Assert.IsNotNull(typeof(MobileThermalProfilerCli).GetMethod("RunMcpAuditJson", Type.EmptyTypes));
        }
    }
}

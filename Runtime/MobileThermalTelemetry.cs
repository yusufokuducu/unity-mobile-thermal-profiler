using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace YusufOkuducu.MobileThermalProfiler
{
    public enum BottleneckKind : byte
    {
        Balanced = 0,
        CpuMainThread = 1,
        CpuRenderThread = 2,
        GpuFragmentOrBandwidth = 3,
        ThermalThrottled = 4,
        VSyncBound = 5
    }

    public enum MobileThermalState : byte
    {
        Nominal = 0,
        Fair = 1,
        Serious = 2,
        Critical = 3
    }

    [Serializable]
    public struct FrameTelemetrySample
    {
        public int frameIndex;
        public float unscaledDeltaTimeMs;
        public float cpuMainThreadMs;
        public float cpuRenderThreadMs;
        public float gpuFrameTimeMs;
        public float thermalHeadroomNormalized; // 0.0 (cool) -> 1.0+ (throttling)
        public long allocatedMemoryBytes;
        public long monoHeapBytes;
        public BottleneckKind bottleneck;
    }

    [Serializable]
    public struct TelemetrySummary
    {
        public int sampleCount;
        public float avgFps;
        public float avgFrameMs;
        public float p50FrameMs;     // Median
        public float p90FrameMs;
        public float p95FrameMs;
        public float p99FrameMs;     // 1% low equivalent
        public float p99_9FrameMs;   // 0.1% low equivalent
        public float onePercentLowFps;
        public float pointOnePercentLowFps;
        public float frameTimeJitterStdDevMs;
        public float avgCpuMs;
        public float avgRenderThreadMs;
        public float avgGpuMs;
        public float peakThermalLoad;
        public float thermalDriftSlopeMsPerSec;
        public MobileThermalState thermalState;
        public int thermalThrottleFrames;
        public int vsyncMissFrames;
        public int longestVSyncDropStreak;
        public int gcAllocationSpikeFrames;
        public long totalMonoHeapDeltaBytes;
        public BottleneckKind dominantBottleneck;
    }

    /// <summary>
    /// Zero-allocation circular buffer and statistical analyzer for Mobile Frame Pacing,
    /// Thermal Throttling frequency drift (OLS slope regression), VSync drop streaks, GC pressure,
    /// and CPU/GPU FrameTimingManager breakdown.
    /// </summary>
    public sealed class MobileThermalTelemetry
    {
        private readonly FrameTelemetrySample[] _ringBuffer;
        private readonly float[] _sortScratch;
        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private int _head;
        private int _count;
        private float _baselineGpuMs;
        private int _baselineSamplesCollected;

        public int Capacity => _ringBuffer.Length;
        public int Count => _count;

        public MobileThermalTelemetry(int capacity = 600)
        {
            if (capacity < 30) capacity = 30;
            _ringBuffer = new FrameTelemetrySample[capacity];
            _sortScratch = new float[capacity];
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            _baselineGpuMs = 0f;
            _baselineSamplesCollected = 0;
        }

        /// <summary>
        /// Captures the current frame's timing and thermal state without allocating GC memory.
        /// Call once per frame from Update or LateUpdate.
        /// </summary>
        public FrameTelemetrySample CaptureFrame(float targetFrameRate = 60f, float externalThermalNormalized = -1f)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint numTimings = FrameTimingManager.GetLatestTimings(1, _frameTimings);

            float dtMs = SanitizeMs(Time.unscaledDeltaTime * 1000f);
            float cpuMain = dtMs;
            float cpuRender = 0f;
            float gpuTime = 0f;

            if (numTimings > 0)
            {
                ref FrameTiming ft = ref _frameTimings[0];
                if (ft.cpuMainThreadFrameTime > 0) cpuMain = SanitizeMs((float)ft.cpuMainThreadFrameTime);
                if (ft.cpuRenderThreadFrameTime > 0) cpuRender = SanitizeMs((float)ft.cpuRenderThreadFrameTime);
                if (ft.gpuFrameTime > 0) gpuTime = SanitizeMs((float)ft.gpuFrameTime);
            }

            // Establish early-session GPU baseline to detect thermal clock frequency drop (DVFS drift)
            if (gpuTime > 0.1f && _baselineSamplesCollected < 120)
            {
                _baselineGpuMs = (_baselineGpuMs * _baselineSamplesCollected + gpuTime) / (_baselineSamplesCollected + 1);
                _baselineSamplesCollected++;
            }

            float safeTargetFps = SanitizeTargetFps(targetFrameRate);

            float thermalLoad = (!float.IsNaN(externalThermalNormalized) && !float.IsInfinity(externalThermalNormalized) && externalThermalNormalized >= 0f)
                ? Mathf.Clamp01(externalThermalNormalized)
                : EstimateThermalDrift(gpuTime, dtMs, safeTargetFps);

            float targetBudgetMs = 1000f / safeTargetFps;

            var sample = new FrameTelemetrySample
            {
                frameIndex = Time.frameCount,
                unscaledDeltaTimeMs = dtMs,
                cpuMainThreadMs = cpuMain,
                cpuRenderThreadMs = cpuRender,
                gpuFrameTimeMs = gpuTime,
                thermalHeadroomNormalized = thermalLoad,
                allocatedMemoryBytes = Math.Max(0L, Profiler.GetTotalAllocatedMemoryLong()),
                monoHeapBytes = Math.Max(0L, Profiler.GetMonoUsedSizeLong()),
                bottleneck = ClassifyBottleneck(dtMs, cpuMain, cpuRender, gpuTime, thermalLoad, targetBudgetMs)
            };

            PushSample(sample);
            return sample;
        }

        public void PushSample(FrameTelemetrySample sample)
        {
            sample.unscaledDeltaTimeMs = SanitizeMs(sample.unscaledDeltaTimeMs);
            sample.cpuMainThreadMs = SanitizeMs(sample.cpuMainThreadMs);
            sample.cpuRenderThreadMs = SanitizeMs(sample.cpuRenderThreadMs);
            sample.gpuFrameTimeMs = SanitizeMs(sample.gpuFrameTimeMs);
            sample.thermalHeadroomNormalized = SanitizeMs(sample.thermalHeadroomNormalized);
            if (sample.allocatedMemoryBytes < 0) sample.allocatedMemoryBytes = 0;
            if (sample.monoHeapBytes < 0) sample.monoHeapBytes = 0;

            _ringBuffer[_head] = sample;
            _head = (_head + 1) % _ringBuffer.Length;
            if (_count < _ringBuffer.Length)
                _count++;
        }

        public FrameTelemetrySample GetSampleChronological(int index)
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int start = _count < _ringBuffer.Length ? 0 : _head;
            int actual = (start + index) % _ringBuffer.Length;
            return _ringBuffer[actual];
        }

        public TelemetrySummary ComputeSummary(float targetFps = 60f)
        {
            var summary = new TelemetrySummary { sampleCount = _count };
            if (_count == 0) return summary;

            float safeTargetFps = SanitizeTargetFps(targetFps);
            float sumFrameMs = 0f;
            float sumCpuMs = 0f;
            float sumRenderMs = 0f;
            float sumGpuMs = 0f;
            float maxThermal = 0f;
            int thermalFrames = 0;
            int vsyncMisses = 0;
            int currentDropStreak = 0;
            int maxDropStreak = 0;
            int gcSpikeFrames = 0;
            long positiveMonoDeltaSum = 0;

            float budgetMs = (1000f / safeTargetFps) * 1.15f;
            Span<int> bottleneckCounts = stackalloc int[6];

            // Variables for Ordinary Least Squares (OLS) thermal drift slope (ms per second of elapsed time)
            double elapsedSec = 0.0;
            double sumX = 0.0;
            double sumY = 0.0;
            double sumXY = 0.0;
            double sumX2 = 0.0;

            long prevMonoBytes = GetSampleChronological(0).monoHeapBytes;

            for (int i = 0; i < _count; i++)
            {
                FrameTelemetrySample s = GetSampleChronological(i);
                float dt = s.unscaledDeltaTimeMs;
                _sortScratch[i] = dt;

                sumFrameMs += dt;
                sumCpuMs += s.cpuMainThreadMs;
                sumRenderMs += s.cpuRenderThreadMs;
                sumGpuMs += s.gpuFrameTimeMs;

                if (s.thermalHeadroomNormalized > maxThermal)
                    maxThermal = s.thermalHeadroomNormalized;
                if (s.thermalHeadroomNormalized >= 0.85f)
                    thermalFrames++;

                if (dt > budgetMs)
                {
                    vsyncMisses++;
                    currentDropStreak++;
                    if (currentDropStreak > maxDropStreak)
                        maxDropStreak = currentDropStreak;
                }
                else
                {
                    currentDropStreak = 0;
                }

                if (i > 0)
                {
                    long monoDelta = s.monoHeapBytes - prevMonoBytes;
                    if (monoDelta > 0)
                    {
                        positiveMonoDeltaSum += monoDelta;
                        if (monoDelta >= 32 * 1024) // >= 32 KB allocation in a single frame
                            gcSpikeFrames++;
                    }
                    prevMonoBytes = s.monoHeapBytes;
                }

                elapsedSec += dt * 0.001;
                double yMetric = s.gpuFrameTimeMs > 0.1f ? s.gpuFrameTimeMs : dt;
                sumX += elapsedSec;
                sumY += yMetric;
                sumXY += elapsedSec * yMetric;
                sumX2 += elapsedSec * elapsedSec;

                int bIdx = (int)s.bottleneck;
                if (bIdx >= 0 && bIdx < 6)
                    bottleneckCounts[bIdx]++;
            }

            Array.Sort(_sortScratch, 0, _count);

            float avgFrameMs = sumFrameMs / _count;
            float varianceSum = 0f;
            for (int i = 0; i < _count; i++)
            {
                float diff = _sortScratch[i] - avgFrameMs;
                varianceSum += diff * diff;
            }

            summary.avgFrameMs = avgFrameMs;
            summary.avgFps = avgFrameMs > 0.001f ? 1000f / avgFrameMs : 0f;
            summary.p50FrameMs = GetPercentileSorted(_sortScratch, _count, 0.50f);
            summary.p90FrameMs = GetPercentileSorted(_sortScratch, _count, 0.90f);
            summary.p95FrameMs = GetPercentileSorted(_sortScratch, _count, 0.95f);
            summary.p99FrameMs = GetPercentileSorted(_sortScratch, _count, 0.99f);
            summary.p99_9FrameMs = GetPercentileSorted(_sortScratch, _count, 0.999f);
            summary.onePercentLowFps = summary.p99FrameMs > 0.001f ? 1000f / summary.p99FrameMs : 0f;
            summary.pointOnePercentLowFps = summary.p99_9FrameMs > 0.001f ? 1000f / summary.p99_9FrameMs : 0f;
            summary.frameTimeJitterStdDevMs = Mathf.Sqrt(varianceSum / _count);
            summary.avgCpuMs = sumCpuMs / _count;
            summary.avgRenderThreadMs = sumRenderMs / _count;
            summary.avgGpuMs = sumGpuMs / _count;
            summary.peakThermalLoad = maxThermal;
            summary.thermalThrottleFrames = thermalFrames;
            summary.vsyncMissFrames = vsyncMisses;
            summary.longestVSyncDropStreak = maxDropStreak;
            summary.gcAllocationSpikeFrames = gcSpikeFrames;
            summary.totalMonoHeapDeltaBytes = positiveMonoDeltaSum;

            double denom = (_count * sumX2) - (sumX * sumX);
            if (_count >= 5 && Math.Abs(denom) > 1e-6)
            {
                summary.thermalDriftSlopeMsPerSec = (float)(((_count * sumXY) - (sumX * sumY)) / denom);
            }

            summary.thermalState = ClassifyThermalState(maxThermal, summary.thermalDriftSlopeMsPerSec, thermalFrames, _count);

            int bestIdx = 0;
            for (int b = 1; b < 6; b++)
            {
                if (bottleneckCounts[b] > bottleneckCounts[bestIdx])
                    bestIdx = b;
            }
            summary.dominantBottleneck = (BottleneckKind)bestIdx;
            return summary;
        }

        private static float GetPercentileSorted(float[] sorted, int count, float percentile)
        {
            if (count <= 0) return 0f;
            int idx = Mathf.Clamp(Mathf.CeilToInt(count * percentile) - 1, 0, count - 1);
            return sorted[idx];
        }

        public static MobileThermalState ClassifyThermalState(
            float peakThermalNormalized,
            float driftSlopeMsPerSec,
            int throttleFrames,
            int totalSamples)
        {
            float throttleRatio = totalSamples > 0 ? (float)throttleFrames / totalSamples : 0f;
            if (peakThermalNormalized >= 0.95f || throttleRatio >= 0.25f)
                return MobileThermalState.Critical;
            if (peakThermalNormalized >= 0.80f || throttleRatio >= 0.05f || driftSlopeMsPerSec >= 0.35f)
                return MobileThermalState.Serious;
            if (peakThermalNormalized >= 0.55f || driftSlopeMsPerSec >= 0.12f)
                return MobileThermalState.Fair;
            return MobileThermalState.Nominal;
        }

        public static BottleneckKind ClassifyBottleneck(
            float frameMs,
            float cpuMainMs,
            float cpuRenderMs,
            float gpuMs,
            float thermalNormalized,
            float targetBudgetMs)
        {
            if (float.IsNaN(targetBudgetMs) || float.IsInfinity(targetBudgetMs) || targetBudgetMs <= 0.1f)
                targetBudgetMs = 16.67f;

            if (thermalNormalized >= 0.85f && frameMs > targetBudgetMs * 1.1f)
                return BottleneckKind.ThermalThrottled;

            if (frameMs <= targetBudgetMs * 1.05f)
            {
                if (cpuMainMs < targetBudgetMs * 0.7f && gpuMs < targetBudgetMs * 0.7f)
                    return BottleneckKind.VSyncBound;
                return BottleneckKind.Balanced;
            }

            if (gpuMs > cpuMainMs && gpuMs > cpuRenderMs && gpuMs > targetBudgetMs * 0.85f)
                return BottleneckKind.GpuFragmentOrBandwidth;

            if (cpuRenderMs > cpuMainMs && cpuRenderMs > targetBudgetMs * 0.8f)
                return BottleneckKind.CpuRenderThread;

            return BottleneckKind.CpuMainThread;
        }

        private float EstimateThermalDrift(float gpuTimeMs, float dtMs, float targetFps)
        {
            float safeFps = SanitizeTargetFps(targetFps);
            float budget = 1000f / safeFps;
            if (_baselineGpuMs > 0.5f && gpuTimeMs > 0.5f)
            {
                float ratio = gpuTimeMs / _baselineGpuMs;
                return Mathf.Clamp01((ratio - 1.0f) / 0.75f);
            }
            return Mathf.Clamp01((dtMs - budget * 0.7f) / (budget * 0.8f));
        }

        private static float SanitizeTargetFps(float targetFps)
        {
            if (float.IsNaN(targetFps) || float.IsInfinity(targetFps) || targetFps <= 0.1f)
                return 60f;
            return targetFps;
        }

        private static float SanitizeMs(float val)
        {
            if (float.IsNaN(val) || float.IsInfinity(val) || val < 0f)
                return 0f;
            return val;
        }

        public string ExportToCsv()
        {
            var sb = new StringBuilder(_count * 64 + 128);
            sb.AppendLine("frame,dt_ms,cpu_main_ms,cpu_render_ms,gpu_ms,thermal_norm,alloc_mb,mono_mb,bottleneck");
            for (int i = 0; i < _count; i++)
            {
                FrameTelemetrySample s = GetSampleChronological(i);
                sb.Append(s.frameIndex).Append(',')
                  .Append(s.unscaledDeltaTimeMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.cpuMainThreadMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.cpuRenderThreadMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.gpuFrameTimeMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.thermalHeadroomNormalized.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append((s.allocatedMemoryBytes / (1024f * 1024f)).ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append((s.monoHeapBytes / (1024f * 1024f)).ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .AppendLine(s.bottleneck.ToString());
            }
            return sb.ToString();
        }

        public string ExportSummaryToMarkdown(float targetFps = 60f)
        {
            float safeFps = SanitizeTargetFps(targetFps);
            TelemetrySummary s = ComputeSummary(safeFps);
            var sb = new StringBuilder(512);
            sb.AppendLine("# Mobile Thermal & Frame-Pacing Telemetry Report");
            sb.AppendLine();
            sb.AppendLine($"- **Samples Analyzed**: {s.sampleCount} (Target: {safeFps.ToString("F0", CultureInfo.InvariantCulture)} FPS)");
            sb.AppendLine($"- **Dominant Bottleneck**: `{s.dominantBottleneck}`");
            sb.AppendLine($"- **Thermal State**: `{s.thermalState}` (Peak Load: {(s.peakThermalLoad * 100f).ToString("F1", CultureInfo.InvariantCulture)}%, Drift Slope: {s.thermalDriftSlopeMsPerSec.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)} ms/s)");
            sb.AppendLine($"- **Average FPS**: {s.avgFps.ToString("F1", CultureInfo.InvariantCulture)} FPS ({s.avgFrameMs.ToString("F2", CultureInfo.InvariantCulture)} ms)");
            sb.AppendLine($"- **1% Low FPS (P99)**: {s.onePercentLowFps.ToString("F1", CultureInfo.InvariantCulture)} FPS ({s.p99FrameMs.ToString("F2", CultureInfo.InvariantCulture)} ms)");
            sb.AppendLine($"- **0.1% Low FPS (P99.9)**: {s.pointOnePercentLowFps.ToString("F1", CultureInfo.InvariantCulture)} FPS ({s.p99_9FrameMs.ToString("F2", CultureInfo.InvariantCulture)} ms)");
            sb.AppendLine($"- **Frame Jitter (StdDev σ)**: {s.frameTimeJitterStdDevMs.ToString("F2", CultureInfo.InvariantCulture)} ms");
            sb.AppendLine($"- **VSync Misses**: {s.vsyncMissFrames}/{s.sampleCount} (Max Consecutive Drop Streak: {s.longestVSyncDropStreak} frames)");
            sb.AppendLine($"- **GC Allocation Spike Frames (>=32KB)**: {s.gcAllocationSpikeFrames}");
            return sb.ToString();
        }

        public void SaveCsvToFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, ExportToCsv(), Encoding.UTF8);
        }
    }
}

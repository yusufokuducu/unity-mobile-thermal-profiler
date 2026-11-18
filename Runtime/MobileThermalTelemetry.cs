using System;
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
        public float p99FrameMs;     // 1% low equivalent
        public float p99_9FrameMs;   // 0.1% low equivalent
        public float onePercentLowFps;
        public float pointOnePercentLowFps;
        public float frameTimeJitterStdDevMs;
        public float avgCpuMs;
        public float avgGpuMs;
        public float peakThermalLoad;
        public int thermalThrottleFrames;
        public int vsyncMissFrames;
        public BottleneckKind dominantBottleneck;
    }

    /// <summary>
    /// Zero-allocation circular buffer and statistical analyzer for Mobile Frame Pacing,
    /// Thermal Throttling drift, and CPU/GPU FrameTimingManager breakdown.
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

            float dtMs = Time.unscaledDeltaTime * 1000f;
            float cpuMain = dtMs;
            float cpuRender = 0f;
            float gpuTime = 0f;

            if (numTimings > 0)
            {
                ref FrameTiming ft = ref _frameTimings[0];
                if (ft.cpuMainThreadFrameTime > 0) cpuMain = (float)ft.cpuMainThreadFrameTime;
                if (ft.cpuRenderThreadFrameTime > 0) cpuRender = (float)ft.cpuRenderThreadFrameTime;
                if (ft.gpuFrameTime > 0) gpuTime = (float)ft.gpuFrameTime;
            }

            // Establish early-session GPU baseline to detect thermal clock frequency drop (drift)
            if (gpuTime > 0.1f && _baselineSamplesCollected < 120)
            {
                _baselineGpuMs = (_baselineGpuMs * _baselineSamplesCollected + gpuTime) / (_baselineSamplesCollected + 1);
                _baselineSamplesCollected++;
            }

            float thermalLoad = externalThermalNormalized >= 0f
                ? externalThermalNormalized
                : EstimateThermalDrift(gpuTime, dtMs, targetFrameRate);

            float targetBudgetMs = targetFrameRate > 0f ? (1000f / targetFrameRate) : 16.67f;

            var sample = new FrameTelemetrySample
            {
                frameIndex = Time.frameCount,
                unscaledDeltaTimeMs = dtMs,
                cpuMainThreadMs = cpuMain,
                cpuRenderThreadMs = cpuRender,
                gpuFrameTimeMs = gpuTime,
                thermalHeadroomNormalized = thermalLoad,
                allocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong(),
                monoHeapBytes = Profiler.GetMonoUsedSizeLong(),
                bottleneck = ClassifyBottleneck(dtMs, cpuMain, cpuRender, gpuTime, thermalLoad, targetBudgetMs)
            };

            PushSample(sample);
            return sample;
        }

        public void PushSample(FrameTelemetrySample sample)
        {
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

            float sumFrameMs = 0f;
            float sumCpuMs = 0f;
            float sumGpuMs = 0f;
            float maxThermal = 0f;
            int thermalFrames = 0;
            int vsyncMisses = 0;
            float budgetMs = targetFps > 0f ? (1000f / targetFps) * 1.15f : 19.2f;

            Span<int> bottleneckCounts = stackalloc int[6];

            for (int i = 0; i < _count; i++)
            {
                FrameTelemetrySample s = GetSampleChronological(i);
                _sortScratch[i] = s.unscaledDeltaTimeMs;
                sumFrameMs += s.unscaledDeltaTimeMs;
                sumCpuMs += s.cpuMainThreadMs;
                sumGpuMs += s.gpuFrameTimeMs;
                if (s.thermalHeadroomNormalized > maxThermal)
                    maxThermal = s.thermalHeadroomNormalized;
                if (s.thermalHeadroomNormalized >= 0.85f)
                    thermalFrames++;
                if (s.unscaledDeltaTimeMs > budgetMs)
                    vsyncMisses++;

                bottleneckCounts[(int)s.bottleneck]++;
            }

            Array.Sort(_sortScratch, 0, _count);

            float avgFrameMs = sumFrameMs / _count;
            float varianceSum = 0f;
            for (int i = 0; i < _count; i++)
            {
                float diff = _sortScratch[i] - avgFrameMs;
                varianceSum += diff * diff;
            }

            int idxP99 = Mathf.Clamp(Mathf.CeilToInt(_count * 0.99f) - 1, 0, _count - 1);
            int idxP999 = Mathf.Clamp(Mathf.CeilToInt(_count * 0.999f) - 1, 0, _count - 1);

            summary.avgFrameMs = avgFrameMs;
            summary.avgFps = avgFrameMs > 0.001f ? 1000f / avgFrameMs : 0f;
            summary.p99FrameMs = _sortScratch[idxP99];
            summary.p99_9FrameMs = _sortScratch[idxP999];
            summary.onePercentLowFps = summary.p99FrameMs > 0.001f ? 1000f / summary.p99FrameMs : 0f;
            summary.pointOnePercentLowFps = summary.p99_9FrameMs > 0.001f ? 1000f / summary.p99_9FrameMs : 0f;
            summary.frameTimeJitterStdDevMs = Mathf.Sqrt(varianceSum / _count);
            summary.avgCpuMs = sumCpuMs / _count;
            summary.avgGpuMs = sumGpuMs / _count;
            summary.peakThermalLoad = maxThermal;
            summary.thermalThrottleFrames = thermalFrames;
            summary.vsyncMissFrames = vsyncMisses;

            int bestIdx = 0;
            for (int b = 1; b < 6; b++)
            {
                if (bottleneckCounts[b] > bottleneckCounts[bestIdx])
                    bestIdx = b;
            }
            summary.dominantBottleneck = (BottleneckKind)bestIdx;
            return summary;
        }

        public static BottleneckKind ClassifyBottleneck(
            float frameMs,
            float cpuMainMs,
            float cpuRenderMs,
            float gpuMs,
            float thermalNormalized,
            float targetBudgetMs)
        {
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
            float budget = targetFps > 0f ? 1000f / targetFps : 16.67f;
            if (_baselineGpuMs > 0.5f && gpuTimeMs > 0.5f)
            {
                float ratio = gpuTimeMs / _baselineGpuMs;
                return Mathf.Clamp01((ratio - 1.0f) / 0.75f);
            }
            return Mathf.Clamp01((dtMs - budget * 0.7f) / (budget * 0.8f));
        }

        public string ExportToCsv()
        {
            var sb = new StringBuilder(_count * 64 + 128);
            sb.AppendLine("frame,dt_ms,cpu_main_ms,cpu_render_ms,gpu_ms,thermal_norm,alloc_mb,mono_mb,bottleneck");
            for (int i = 0; i < _count; i++)
            {
                FrameTelemetrySample s = GetSampleChronological(i);
                sb.Append(s.frameIndex).Append(',')
                  .Append(s.unscaledDeltaTimeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.cpuMainThreadMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.cpuRenderThreadMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.gpuFrameTimeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.thermalHeadroomNormalized.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append((s.allocatedMemoryBytes / (1024f * 1024f)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append((s.monoHeapBytes / (1024f * 1024f)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .AppendLine(s.bottleneck.ToString());
            }
            return sb.ToString();
        }

        public void SaveCsvToFile(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, ExportToCsv(), Encoding.UTF8);
        }
    }
}

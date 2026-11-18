# Unity Mobile Thermal, Frame-Pacing & TBDR Bandwidth Profiler

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A specialized, **zero-allocation** mobile performance diagnostics toolkit for Unity (`2022.3 LTS` and `Unity 6`). Instead of generic FPS counters that hide micro-stutters and thermal throttling, this package targets the three primary causes of mobile performance degradation on **ARM Mali, Qualcomm Adreno, and Apple GPUs**:

1. **TBDR (Tile-Based Deferred Rendering) DRAM Bandwidth Audits**: Calculates per-frame external memory traffic caused by unresolved MSAA targets (`bindTextureMS`), 64-bit `R16G16B16A16_SFloat` HDR buffers, persistent Depth/Stencil stores, and per-frame mipmap generation.
2. **Frame-Pacing & Jitter Telemetry (1% / 0.1% Lows)**: Pre-allocated ring-buffer statistical engine tracking P99 (`1% Low`), P99.9 (`0.1% Low`), frame-time standard deviation ($\sigma$), and VSync misses with zero GC allocations.
3. **Thermal Frequency-Drift & Bottleneck Classifier**: Correlates `FrameTimingManager` CPU Main Thread, Render Thread, and GPU frame times against early-session GPU baselines to classify whether frames are **CPU-bound**, **GPU Fragment/Bandwidth-bound**, **VSync-bound**, or **Thermally Throttled**.

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-mobile-thermal-profiler.git
```

---

## Quick Start

### 1. Editor TBDR Bandwidth Auditor
Open **Window -> Analysis -> Mobile Thermal & TBDR Bandwidth Profiler** and click **Audit Active RenderTextures (TBDR Bandwidth)** to inspect every allocated `RenderTexture`, its estimated MB/frame tile flush cost, GB/s bandwidth at 60/120 FPS, and actionable fixes.

### 2. On-Device HUD Overlay & CSV Export
Attach `MobileThermalHudOverlay` to any GameObject (or click **Add Runtime HUD Overlay to Scene** in the Editor window). You can also drive `MobileThermalTelemetry` directly from code:

```csharp
using YusufOkuducu.MobileThermalProfiler;

public class GamePerfMonitor : MonoBehaviour
{
    private MobileThermalTelemetry _telemetry = new MobileThermalTelemetry(600);

    void Update()
    {
        FrameTelemetrySample sample = _telemetry.CaptureFrame(targetFrameRate: 60f);
    }

    public void DumpSessionReport(string path)
    {
        TelemetrySummary summary = _telemetry.ComputeSummary(60f);
        Debug.Log($"1% Low FPS: {summary.onePercentLowFps:F1}, Dominant: {summary.dominantBottleneck}");
        _telemetry.SaveCsvToFile(path);
    }
}
```

## License

MIT License — see [LICENSE](LICENSE).

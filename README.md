# Unity Mobile Thermal, Frame-Pacing & TBDR Bandwidth Profiler

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A specialized, **zero-allocation** mobile performance diagnostics toolkit for Unity (`2022.3 LTS` and `Unity 6`). Instead of generic FPS counters that hide micro-stutters and thermal throttling, this package targets the primary causes of mobile performance degradation on **ARM Mali, Qualcomm Adreno, and Apple GPUs**:

1. **TBDR (Tile-Based Deferred Rendering) DRAM Bandwidth & Power Auditor (`TbdrBandwidthAuditor`)**
   - Calculates per-frame external memory traffic (`MB/frame`, `GB/s` at 60/120 FPS) and estimated **LPDDR4X/LPDDR5 DRAM power draw (`mW`)** (~105 pJ/byte external transfer model) caused by unresolved MSAA targets (`bindTextureMS`), 64-bit `R16G16B16A16_SFloat` HDR buffers, persistent Depth/Stencil stores, and per-frame mipmap generation.
   - Includes a **URP Camera Pipeline Bandwidth & Power Simulator** (`EstimateUrpCameraPipelineBandwidth`) that models `_CameraDepthTexture`, `_CameraOpaqueTexture` mid-frame tile flushes, Shadowmap atlases, and Bloom pyramids against target SoC thermal envelopes (`EntryLevelBudget`, `MidRangeMainstream`, `FlagshipHighEnd`).
2. **Frame-Pacing, Jitter & VSync Streak Telemetry (`MobileThermalTelemetry`)**
   - Pre-allocated ring-buffer statistical engine tracking Median (`P50`), `P90`, `P95`, `P99` (`1% Low`), `P99.9` (`0.1% Low`), frame-time standard deviation ($\sigma$), consecutive VSync drop streaks, and single-frame GC allocation spikes (`>= 32 KB`) with zero GC allocations.
3. **OLS Thermal Drift Slope & Bottleneck Classifier**
   - Computes Ordinary Least Squares (OLS) linear regression slope (`ms/sec`) over the telemetry window to detect active thermal throttling (`Nominal`, `Fair`, `Serious`, `Critical`) and classifies whether frames are **CPU Main-Thread**, **CPU Render-Thread**, **GPU Fragment/Bandwidth**, **VSync**, or **Thermally Throttled**.

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-mobile-thermal-profiler.git
```

---

## Quick Start

### 1. Editor TBDR Bandwidth Auditor & URP Pipeline Simulator
Open **Window -> Analysis -> Mobile Thermal & TBDR Bandwidth Profiler** to:
- Audit every allocated `RenderTexture`, its estimated MB/frame tile flush cost, GB/s bandwidth at 60/120 FPS, mW DRAM power draw, and actionable fixes.
- Simulate URP camera settings (`_CameraDepthTexture`, `_CameraOpaqueTexture`, HDR format, Bloom) against mobile SoC thermal budgets.

### 2. On-Device HUD Overlay & CSV/Markdown Export
Attach `MobileThermalHudOverlay` to any GameObject (or click **Add Runtime HUD Overlay to Scene** in the Editor window), or drive `MobileThermalTelemetry` directly from code:

```csharp
using YusufOkuducu.MobileThermalProfiler;

public class GamePerfMonitor : MonoBehaviour
{
    private readonly MobileThermalTelemetry _telemetry = new MobileThermalTelemetry(600);

    void Update()
    {
        FrameTelemetrySample sample = _telemetry.CaptureFrame(targetFrameRate: 60f);
    }

    public void DumpSessionReport(string csvPath)
    {
        TelemetrySummary summary = _telemetry.ComputeSummary(60f);
        Debug.Log($"1% Low: {summary.onePercentLowFps:F1} FPS | Thermal: {summary.thermalState} ({summary.thermalDriftSlopeMsPerSec:+0.000;-0.000;0.000} ms/s)");
        _telemetry.SaveCsvToFile(csvPath);
    }
}
```

### 3. Headless CI / Batchmode Support (`MobileThermalProfilerCli`)
Run automated TBDR bandwidth and URP pipeline simulations in CI/CD pipelines:
```bash
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileThermalProfiler.Editor.MobileThermalProfilerCli.RunCiAudit
```

---

## Running Standalone Unit Tests (.NET CLI)

```bash
dotnet test DotNetTests~/MobileThermalProfiler.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).

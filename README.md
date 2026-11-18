# Unity Mobile Thermal, Frame-Pacing & TBDR Bandwidth Profiler

[![CI](https://github.com/yusufokuducu/unity-mobile-thermal-profiler/actions/workflows/ci.yml/badge.svg)](https://github.com/yusufokuducu/unity-mobile-thermal-profiler/actions/workflows/ci.yml)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![Version](https://img.shields.io/badge/version-1.2.0-brightgreen.svg)](CHANGELOG.md)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A specialized, **zero-allocation** mobile performance diagnostics, UI Toolkit thermal heatmap, and MCP / AI Agent auto-tuner toolkit for Unity (`2022.3 LTS` and `Unity 6`). Instead of generic FPS counters that hide micro-stutters and thermal throttling, this package targets the primary causes of mobile performance degradation on **ARM Mali, Qualcomm Adreno, and Apple GPUs**:

1. **TBDR (Tile-Based Deferred Rendering) DRAM Bandwidth & Power Auditor (`TbdrBandwidthAuditor`)**
   - Calculates per-frame external memory traffic (`MB/frame`, `GB/s` at 60/120 FPS) and estimated **LPDDR4X/LPDDR5 DRAM power draw (`mW`)** (~105 pJ/byte external transfer model) caused by unresolved MSAA targets (`bindTextureMS`), 64-bit `R16G16B16A16_SFloat` HDR buffers, persistent Depth/Stencil stores, and per-frame mipmap generation.
   - Includes a **URP Camera Pipeline Bandwidth & Power Simulator** (`EstimateUrpCameraPipelineBandwidth`) that models `_CameraDepthTexture`, `_CameraOpaqueTexture` mid-frame tile flushes, Shadowmap atlases, and Bloom pyramids against target SoC thermal envelopes (`EntryLevelBudget` 2.5 GB/s, `MidRangeMainstream` 4.5 GB/s, `FlagshipHighEnd` 7.5 GB/s).
2. **Modern UI Toolkit (`CreateGUI`) LPDDR Bandwidth & Thermal Envelope Heatmap (`ThermalBandwidthProfilerWindow`)**
   - Color-coded thermal budget utilization gauge (`ProgressBar`), per-RenderTexture tile flush bandwidth bars, interactive search filtering, and one-click **Auto-Tune URP for SoC Budget**.
3. **MCP (Model Context Protocol) & AI Agent Auto-Tune Bridge (`MobileThermalProfilerMcpBridge`)**
   - Built-in JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) compatible with **Cursor**, **Claude Desktop**, **Antigravity**, and Unity MCP servers (`protocolVersion: "2024-11-05"`):
     - `thermal_profiler.audit_render_textures`: Audits allocated `RenderTexture` objects and returns structured JSON bandwidth & mW power metrics.
     - `thermal_profiler.simulate_urp_pipeline`: Simulates URP camera pass bandwidth and thermal envelope status.
     - `thermal_profiler.autotune_urp_pipeline`: Automatically tunes URP settings (`B10G11R11_UFloatPack32` HDR, disabling `_CameraOpaqueTexture` / `_CameraDepthTexture` copies, shadow atlas scaling) to fit the target `MobileSoCTier` DRAM budget.
     - `thermal_profiler.autofix_render_textures`: Automatically disables `bindTextureMS`, converts 64-bit HDR formats to 32-bit `R11G11B10`, and disables per-frame `autoGenerateMips`.
4. **Frame-Pacing, Jitter & VSync Streak Telemetry (`MobileThermalTelemetry`)**
   - Pre-allocated ring-buffer statistical engine tracking Median (`P50`), `P90`, `P95`, `P99` (`1% Low`), `P99.9` (`0.1% Low`), frame-time standard deviation ($\sigma$), consecutive VSync drop streaks, and single-frame GC allocation spikes (`>= 32 KB`) with zero GC allocations.
5. **OLS Thermal Drift Slope & Bottleneck Classifier**
   - Computes Ordinary Least Squares (OLS) linear regression slope (`ms/sec`) over the telemetry window to detect active thermal throttling (`Nominal`, `Fair`, `Serious`, `Critical`) and classifies whether frames are **CPU Main-Thread**, **CPU Render-Thread**, **GPU Fragment/Bandwidth**, **VSync**, or **Thermally Throttled**.

---

## Installation (Unity Package Manager & OpenUPM)

### Via Git URL
Add in **Window -> Package Manager -> + -> Add package from git URL...**:
```text
https://github.com/yusufokuducu/unity-mobile-thermal-profiler.git
```

### Via OpenUPM CLI
```bash
openupm add com.yusufokuducu.mobile-thermal-profiler
```

---

## Quick Start

### 1. Editor TBDR Bandwidth Auditor & URP Pipeline Simulator
Open **Window -> Analysis -> Mobile Thermal & TBDR Bandwidth Profiler** to:
- Audit every allocated `RenderTexture`, its estimated MB/frame tile flush cost, GB/s bandwidth at 60/120 FPS, mW DRAM power draw, and actionable fixes.
- Simulate and auto-tune URP camera settings (`_CameraDepthTexture`, `_CameraOpaqueTexture`, HDR format, Bloom) against mobile SoC thermal budgets.

### 2. Headless CI / Batchmode Support (`MobileThermalProfilerCli`)
```bash
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileThermalProfiler.Editor.MobileThermalProfilerCli.RunCiAudit
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileThermalProfiler.Editor.MobileThermalProfilerCli.RunMcpAuditJson
```

---

## Running Standalone Unit Tests (.NET CLI)

```bash
dotnet test DotNetTests~/MobileThermalProfiler.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).

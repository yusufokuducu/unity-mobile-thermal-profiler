# Changelog

All notable changes to `com.yusufokuducu.mobile-thermal-profiler` are documented in this file.

## [1.2.0] - 2026-11-18

### Added
- **Modern UI Toolkit (`CreateGUI`) LPDDR Bandwidth & Thermal Budget Heatmap Dashboard**:
  - Upgraded `ThermalBandwidthProfilerWindow` with `UnityEngine.UIElements` (`VisualElement`, `ProgressBar` thermal envelope gauge, per-RenderTexture tile-flush traffic bars, interactive search filter, and one-click URP SoC Auto-Tune) while maintaining seamless `OnGUI` fallback compatibility.
- **MCP (Model Context Protocol) & AI Agent JSON-RPC 2.0 Bridge (`MobileThermalProfilerMcpBridge`)**:
  - Full JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) supporting `initialize`, `tools/list`, and `tools/call` for AI coding assistants (Cursor, Claude, Antigravity).
  - Implemented `thermal_profiler.audit_render_textures`, `thermal_profiler.simulate_urp_pipeline`, `thermal_profiler.autotune_urp_pipeline`, and `thermal_profiler.autofix_render_textures`.
  - Added automated remediation (`AutoFixRenderTextures` and `AutoTuneUrpPipelineForSoCTier`) that disables `bindTextureMS`, converts 64-bit `R16G16B16A16_SFloat` buffers to `B10G11R11_UFloatPack32`, disables `_CameraOpaqueTexture` / `_CameraDepthTexture` tile flushes, and scales shadow atlases to fit target SoC thermal budgets.
  - Added `MobileThermalProfilerCli.RunMcpAuditJson()` batchmode entrypoint exporting `Logs/MobileThermalProfilerMcpReport.json`.
- **GitHub Actions CI & OpenUPM Metadata**:
  - Added `.github/workflows/ci.yml` running the standalone `.NET 8` NUnit test suite on every push and pull request.
  - Added OpenUPM-compliant metadata (`documentationUrl`, `changelogUrl`, `licensesUrl`) to `package.json`.

## [1.1.0] - 2026-11-18

### Fixed
- Hardened `MobileThermalTelemetry` against `NaN`, `Infinity`, and negative frame-timing or memory inputs.
- Sanitized `targetFps` across `CaptureFrame`, `ComputeSummary`, and `ExportSummaryToMarkdown`.

## [1.0.0] - 2026-11-18

### Added
- Initial release of `MobileThermalTelemetry`, `TbdrBandwidthAuditor`, `MobileThermalHudOverlay`, `ThermalBandwidthProfilerWindow`, and `MobileThermalProfilerCli`.

# HDMI Capture Card Monitor

A premium Windows monitor application for USB HDMI capture cards. Phase 4 adds borderless fullscreen monitor mode to the production shell around the real Phase 2A GPU preview path. The selected device and exact native mode feed a synchronous Media Foundation reader on a dedicated MTA thread, then GPU-backed NV12 surfaces are converted, Fit-scaled, and presented through Direct3D 11 to the same preview HWND in windowed and fullscreen presentation.

Fullscreen changes only the existing `MainWindow` geometry: it does not restart capture, create another window, reparent the preview child, or replace the GPU renderer. Audio, recording, snapshots, reconnect, DirectShow fallback, and HDMI-specific compatibility claims are not implemented. The built-in HD Camera validates the generic Media Foundation UVC path; a physical USB HDMI capture card remains mandatory before release.

## Build

```powershell
dotnet restore
dotnet build HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
dotnet test HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64 --filter "Category!=Hardware"
```

The ordinary suite explicitly excludes opt-in physical-hardware tests. To rerun Phase 1 discovery validation locally with a connected video device, set `HDMI_CAPTURE_HARDWARE_VALIDATION=1` and use `--filter "Category=Hardware"`. Phase 2A preview validation is an interactive manual procedure documented in `docs/PHASE_2_LOW_LATENCY_PREVIEW.md`.

The app follows the Windows light or dark application preference at startup. Settings and Help are honest in-window information panels. With a live frame visible, select Fullscreen or press F11 to enter or exit monitor mode; Escape exits fullscreen. Snapshot and Record remain visibly unavailable. See `docs/PHASE_4_FULLSCREEN_MONITOR_MODE.md` for design, validation, and remaining hardware boundaries.

The app targets stable .NET 10, Windows, x64, and `win-x64`. See `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `QA_MATRIX.md`, and `CODEX_RULES.md` before making changes.

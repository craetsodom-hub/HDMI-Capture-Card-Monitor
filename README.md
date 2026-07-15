# HDMI Capture Card Monitor

A premium Windows monitor application for USB HDMI capture cards. The application has a real low-latency GPU preview path, borderless fullscreen monitor mode, and an optional Phase 5 WASAPI audio-monitoring path. The selected device and exact native mode feed a synchronous Media Foundation reader on a dedicated MTA thread, then GPU-backed NV12 surfaces are converted, Fit-scaled, and presented through Direct3D 11 to the same preview HWND in windowed and fullscreen presentation.

Audio monitoring is opt-in: the customer selects a capture endpoint or leaves `No audio` selected, chooses an explicit output or `System default output`, and controls local mute and volume. It uses an event-driven shared-mode WASAPI worker, a bounded complete-frame ring buffer, and independent cleanup from the video session. The latest ten-minute muted laptop run completed and released both endpoints but recorded one nine-underrun burst; overruns and dropped frames remained zero, and twenty repeated lifecycle cycles released both endpoints. Audible headphone quality, HDMI A/V synchronization, and physical USB HDMI audio remain unvalidated, so Phase 5 remains draft.

Fullscreen changes only the existing `MainWindow` geometry: it does not restart capture, create another window, reparent the preview child, replace the GPU renderer, or restart audio. Recording, snapshots, reconnect, DirectShow fallback, and HDMI-specific compatibility claims are not implemented. The built-in HD Camera validates the generic Media Foundation UVC video path; a physical USB HDMI capture card remains mandatory before release.

## Build

```powershell
dotnet restore
dotnet build HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64
dotnet test HDMI-Capture-Card-Monitor.sln --configuration Release -p:Platform=x64 --filter "Category!=Hardware"
```

The ordinary suite explicitly excludes opt-in physical-hardware tests. To rerun hardware integration tests locally, set `HDMI_CAPTURE_HARDWARE_VALIDATION=1` and use `--filter "Category=Hardware"`; use headphones before any unmuted microphone-monitoring test to prevent acoustic feedback. Phase 2A preview validation is documented in `docs/PHASE_2_LOW_LATENCY_PREVIEW.md`, and the Phase 5 audio design, corrected evidence, and release boundaries are documented in `docs/PHASE_5_LOW_LATENCY_AUDIO_MONITORING.md`.

The app follows the Windows light or dark application preference at startup. Settings and Help are honest in-window information panels. With a live frame visible, select Fullscreen or press F11 to enter or exit monitor mode; Escape exits fullscreen. Snapshot and Record remain visibly unavailable. See `docs/PHASE_4_FULLSCREEN_MONITOR_MODE.md` for design, validation, and remaining hardware boundaries.

The app targets stable .NET 10, Windows, x64, and `win-x64`. See `PRODUCT_SPEC.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `QA_MATRIX.md`, and `CODEX_RULES.md` before making changes.

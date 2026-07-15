# Phase 4: Fullscreen Monitor Mode

## Product rationale and scope

Fullscreen turns the existing live preview into a clean monitor-filling presentation. It is intentionally a window-presentation feature, not a new capture mode: the active Media Foundation source reader, selected native format, D3D11 device, swap chain, session identity, diagnostic counters, `MainWindow`, `HwndPreviewSurface`, and native preview child remain the same.

Only F11/Escape presentation, exact placement restoration, current-monitor geometry, cursor inactivity, lifecycle exit, and related accessibility are in scope. Audio, snapshots, recording, always-on-top, zoom, image adjustment, reconnect, exclusive DirectX fullscreen, additional windows, sessions, or preview HWNDs are not implemented. Snapshot and Record remain disabled and labelled as upcoming.

## Presentation architecture

- `MainWindowViewModel` exposes eligibility, command text, presentation properties, and automatic exit requests. Fullscreen is enabled only after a real frame is visible and the preview surface is available and presentable.
- `FullscreenWindowController` serializes entry and exit independently of capture. A desired-state worker and monotonic generation prevent stale completion from overwriting a newer Stop, Escape, failure, display-change, close, or disposal request.
- `WpfFullscreenWindowAdapter` connects the controller to the existing `MainWindow` and reapplies the native light/dark title-bar presentation on restore.
- `WindowNativeApi` is the testable Win32 seam for snapshot, monitor selection, borderless placement, exact restore, and safe fallback.
- `FullscreenCursorController` owns an injected one-shot inactivity timer. `HwndPreviewSurface` reports activity from its child-window messages and hides only that fullscreen cursor.

The controller does not reference `ICapturePreviewService`. Source scans additionally guard against introducing preview Start/Stop, `SetParent`, `ShowCursor`, or a second HWND into this presentation layer.

## Placement, monitor, and DPI handling

Before entry, the adapter saves `WINDOWPLACEMENT`, normal bounds, WPF and native styles, extended style, resize mode, state, Topmost value, monitor handle, monitor and work-area rectangles, and `GetDpiForWindow` output. These rectangles stay in signed native physical pixels; they are never round-tripped through WPF DIPs. This preserves monitors positioned left of or above the primary display.

`MonitorFromWindow` with `MONITOR_DEFAULTTONEAREST` selects the current monitor. Fullscreen normalizes the WPF window state, applies `WindowStyle=None` and `ResizeMode=NoResize`, and uses `SetWindowPos` with frame-change flags over the monitor's full `rcMonitor`, not its work area. It never enables persistent Topmost and does not use exclusive DirectX fullscreen.

Exit restores the original native style and `WINDOWPLACEMENT`, followed by the saved WPF chrome and native title-bar theme. A restored window returns to its exact normal bounds and a maximized window returns maximized without accumulating conversion drift. If exact restoration cannot complete, the adapter clamps a safe visible windowed rectangle inside the nearest current work area. A display-configuration notification requests fullscreen exit so removal of the active monitor cannot strand the window off screen.

## Renderer and capture continuity

The original child HWND remains parented to the same `HwndPreviewSurface`. Normal `HwndHost` size notifications flow into the existing capacity-one renderer resize mailbox; swap-chain resizing and Fit-rectangle calculation continue on the preview thread. The UI thread never takes ownership of D3D resources. Black remains the background around non-matching content and fullscreen removes window padding, card chrome, borders, and corner radii without stretching the video.

Fullscreen entry and exit never call preview Start or Stop, reopen the camera, negotiate a media type, reset diagnostics, recreate a child window, or recreate the D3D device. Stop is explicitly ordered as fullscreen exit, geometry restore, preview stop, then `DeviceReady`. Runtime failure and device removal exit presentation before failed-session cleanup. Close first restores presentation, then disposes the view model so capture resources are released before WPF destroys the child HWND. Restoration failure is contained and cannot prevent cleanup.

## Keyboard, accessibility, and cursor

F11 enters or exits fullscreen when eligible and is ignored during a transition. Escape first exits fullscreen; when windowed it closes Settings or Help; otherwise it does nothing. Neither key starts or stops preview. Alt+F4 retains normal safe shutdown behavior. The Fullscreen action has a stable Automation name, honest command text, and Help explains the active-preview requirement, F11/Escape behavior, and uninterrupted capture.

The cursor starts visible. WPF and child-HWND pointer activity restart a deterministic two-second dispatcher timer. On inactivity, `WM_SETCURSOR`/`WM_MOUSEMOVE` handling applies `SetCursor(NULL)` only over the fullscreen preview. Activity, transitions, exit, failure, and disposal stop the timer and restore the cursor. There is no global hook, polling, `Task.Delay`, or `ShowCursor` counter manipulation.

## Failure handling

An entry failure rolls back any partial style or placement mutation and leaves the live windowed preview eligible for retry. It does not fault the capture state. Exact-exit failure uses the nearest-work-area fallback and logs technical context while customer text remains generic. Observer, command, timer, display-change, and closing paths contain exceptions so presentation failure cannot escape application shutdown.

## Validation boundaries

Hardware-independent tests cover eligibility, command continuity, transition serialization, rollback, fallback, exact and maximized restoration, negative coordinates, repeated cycles, stale operations, Stop/failure ordering, disposal, layout semantics, cursor inactivity, and the absence of capture or cursor-counter calls from fullscreen code.

Manual validation uses HD Camera only to exercise generic UVC Media Foundation capture and the existing hardware-D3D11 renderer through windowed/fullscreen transitions. It does not validate HDMI compatibility, HDMI no-signal behavior, capture-card disconnect semantics, or HDMI latency. A physical USB HDMI capture card remains mandatory before Microsoft Store release. Secondary-monitor, unavailable-monitor, high-contrast, WARP, and interlaced/mixed-input results are reported only when those configurations are genuinely exercised.

## Validation record — 2026-07-15

- Release x64 restore/build completed with zero warnings and zero errors; all 297 ordinary hardware-independent tests passed.
- HD Camera exposed 15 native formats. The principal run used 1280 × 720p at advertised 30 fps NV12 through the existing real Media Foundation/hardware-D3D11 path. Actual delivery remained approximately 10 fps; no 30-fps performance claim is made.
- Preview session `4080b0b664ab426086f25400ad21a09a` and child HWND `0xCA0B10` remained identical before, during, and after 30 complete F11 cycles, 20 immediate F11/Escape alternations, resized and maximized restoration tests, minimize/restore, and one continuous 613.3-second fullscreen interval. Selection and diagnostics remained continuous.
- The principal run finished at 10.0 received / 10.0 rendered fps. Across its 108 logged transition completions, controller timing ranged from 43.5 to 301.4 ms with a 74.5 ms average. There were no logger warnings, presentation failures, or D3D device-removal events.
- A resized native placement at `130,66–1143,846` restored to exactly the same `WINDOWPLACEMENT`. The maximized case returned with `showCmd=3` while retaining those normal bounds. No visible drift accumulated.
- Alt+Tab completed normally. A 10.5-second minimize produced intentional presentation skips while input continued; rendered cadence returned to 10.0 fps after restore.
- A native cursor probe after fullscreen inactivity reported `CURSOR_SHOWING` clear. Entry began visible, pointer activity restored it, and every exit/close path left it visible. Screenshot tooling draws its own pointer overlay, so the native probe is the cursor-visibility authority.
- Dark and light application themes were reviewed. Real live fullscreen entry and restoration were exercised at 100%, 150%, and 200% scaling on the 2160 × 1440 built-in display. The original dark/150% user configuration was restored.
- Only one monitor was available. Secondary-monitor placement, physical negative-coordinate monitor placement, and active-monitor removal were therefore not manually performed; signed-coordinate, nearest-monitor fallback, and display-change priority exit remain covered by deterministic tests.
- Windows Camera acquired HD Camera after Stop and again after application close. Close from fullscreen restored presentation in 99.5 ms, then stopped and released capture before the child HWND was destroyed. Final diagnostics reported 1,456 received and 1,456 rendered frames, zero presentation failures, and clean application shutdown.
- A real preview failure or display removal was not deliberately forced. Rollback, safe fallback, runtime-failure exit, Stop-before-cleanup, display-change exit, and disposal-during-entry use deterministic barrier/fake tests. Because fullscreen intentionally contains no WPF overlay controls, the automatic Stop ordering is test evidence; the manual hardware Stop/release check was performed after returning windowed.
- Privacy-safe screenshots are retained outside the repository at `phase4-windowed-preview.png`, `phase4-fullscreen-preview.png`, `phase4-fullscreen-cursor-hidden.png`, and `phase4-restored-window.png`. The camera presented a neutral black subject, so a distinct letterbox boundary was not visually available even though Fit geometry remained active.
- Physical USB HDMI capture-card validation, HDMI no-signal/disconnect behavior, HDMI latency, WARP, high contrast, and interlaced/mixed input remain outstanding. USB HDMI validation is mandatory before Microsoft Store release.

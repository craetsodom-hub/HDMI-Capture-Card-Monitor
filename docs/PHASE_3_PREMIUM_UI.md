# Phase 3: Premium Production UI

## Scope

Phase 3 replaces the engineering shell with a focused Windows monitor layout while preserving the real Media Foundation and hardware-D3D11 preview architecture. The preview remains dominant, device and native-format selectors remain explicit, and Start/Stop remains the only enabled capture action.

The interface contains:

- a compact product header with Settings and Help access keys;
- a state-aware preview surface with plain-language icon, title, and recovery guidance;
- device and native-format selectors with readable empty states, truncation, and full-value tooltips;
- a clear primary Start/Stop action;
- deliberately disabled Fullscreen, Snapshot, and Record controls grouped beneath a visible `UPCOMING` label and labelled as upcoming to accessibility clients;
- a live status region with lifecycle and measured received/rendered FPS when diagnostics are available;
- in-window Settings and Help information panels with no fake toggles or stored preferences.

Audio, recording, snapshots, fullscreen behavior, reconnect, image controls, payment, telemetry, capture-backend redesign, and HDMI-specific logic are outside this phase.

## Design system and themes

`Resources/Themes/DesignTokens.xaml` owns spacing and radius tokens. `Light.xaml` and `Dark.xaml` provide the same semantic color and brush keys. `Controls.xaml` owns shared typography, cards, buttons, selectors, focus states, preview messaging, status, and dialog styling. The application reads the Windows application light/dark preference at startup and falls back to light when that preference cannot be read.

Primary and muted text palettes are automatically checked against their surfaces at WCAG normal-text contrast thresholds. Disabled controls use a separate readable palette and explicit Automation names rather than opacity-only communication. Reduced motion is naturally respected because the shell adds no decorative animation.

## Responsive and accessible interaction

At widths below 900 device-independent pixels, device and format selectors stack and action buttons move below their explanatory text. The supported minimum window is 720 by 620 DIPs. The preview keeps a 220-DIP minimum height. Long device names, format labels, and status text trim without pushing controls outside their grids, and the complete values remain available through tooltips.

Tab navigation follows header actions, selectors, Refresh, capture actions, and information-panel actions. Alt access keys are available for Settings, Help, Device, Format, Refresh, Start/Stop, and the dialog acknowledgement. Important controls have stable Automation names, and preview/status changes are polite live regions. An information panel disables the named main-content container and gates every underlying command, its two actions form a contained Tab cycle, and Escape closes it. Focus is captured before the main content is disabled and restored to the original control when it remains safe, with a selector/Start fallback otherwise.

## HWND airspace and lifetime safety

The Phase 2A `HwndPreviewSurface` remains a single stable child HWND and is never replaced by a WPF bitmap path. WPF placeholder content collapses once the first frame is presented, so it cannot cover live video. One view-model presentation method is the production authority for native visibility: the child may be shown only while the lifecycle is `Previewing`, the information panel is closed, the surface is available and presentable, and the view model is not disposed. First-frame, failure, Stop, availability, panel, and disposal paths update that policy instead of exposing the child directly. The capture session continues behind a panel and the existing synchronous bounded shutdown path remains responsible for device and GPU cleanup.

## State coverage

The shell presents honest messages for scanning, no devices, device selection, format discovery, ready, starting, previewing, stopping, stopped, access denied, busy device, unavailable device, unsupported format/decoder/GPU buffer, stalled input, GPU initialization/removal, startup timeout, and shutdown timeout. User-facing text does not expose HRESULT values, symbolic links, or internal enum names.

## Validation boundaries

Ordinary tests validate the semantic theme contract, both palettes, text contrast, theme preference resolution, responsive breakpoint, automation labels, keyboard access text, informational content, disabled future controls, and the existing capture lifecycle/race suite without hardware.

Manual validation uses the built-in HD Camera to confirm that the redesigned surface still hosts the real Media Foundation/D3D11 preview, reports measured FPS, resizes safely, survives minimize/restore, stops and restarts, releases the camera, and closes cleanly. Screenshots are stored outside the repository and use a neutral subject.

A webcam validates generic UVC behavior only. Physical USB HDMI capture-card compatibility, HDMI no-signal behavior, disconnect recovery, HDMI latency, WARP, interlaced/mixed input, and broader Windows/DPI/high-contrast hardware coverage remain outstanding. USB HDMI hardware validation is mandatory before Microsoft Store release.

## Validation record — 2026-07-15

- Release x64 build completed with zero warnings and zero errors.
- 244 ordinary hardware-independent tests passed.
- HD Camera enumerated 15 native formats; 1280 × 720p at advertised 30/1 NV12 was selected.
- The real Media Foundation/hardware-D3D11 preview presented successfully. After stabilization the UI reported approximately 10.0 received and 10.0 rendered fps; no 30-fps delivery claim is made.
- Start, Stop, restart, maximize/restore, 720-DIP minimum-width layout, and an 11-second minimize/restore completed without stale WPF content covering live video.
- Settings and Help were opened by access key over an active session. The stable HWND host was hidden, the panel received focus, Escape closed it, and the same live session resumed.
- Windows Camera acquired HD Camera after Stop and again after HDMI Capture Card Monitor closed, confirming device release in both paths.
- The active Windows dark-app preference was reviewed manually. Both light and dark resource dictionaries and their contrast thresholds were validated automatically; a manual light-theme and high-contrast review remains outstanding.
- Screenshots were retained outside the repository. No personal screenshot or captured media is committed.

## Final correction validation — 2026-07-15

- 257 ordinary hardware-independent tests passed, including deterministic first-frame, failure, Stop, panel-close, disposal, status-preservation, modal-command, and dynamic-hint combinations.
- Release x64 restore and build completed with zero warnings and zero errors.
- Opening Settings or Help left the lifecycle `StatusMessage` unchanged in Idle, Starting, Previewing/FPS, and stopped checks; lifecycle transitions remained free to update it normally.
- Keyboard-only Settings and Help workflows confirmed initial dialog focus, a forward/reverse contained Tab cycle, Escape close, access-key blocking behind the overlay, and restoration to the originating header action.
- HD Camera preview confirmed the native child hides for Settings/Help during Starting and Previewing, stays hidden when the first frame arrives behind Settings, returns after panel close, and remains hidden after Stop completes behind Settings. Closing the application with a panel open completed safely.
- Dark and light application themes were reviewed manually. Display scaling was changed through Windows Settings and reviewed at 100%, 125%, 150%, and 200%; the user's original dark/150% configuration was restored afterward.
- The 720-DIP minimum width, maximized layout, narrow Settings/Help panels, and the high-DPI startup clamp were reviewed without clipped text, overlap, lost selectors/status, out-of-window dialogs, or native HWND airspace over WPF content.
- High-contrast support was not validated and remains explicitly deferred. Physical USB HDMI capture-card validation remains mandatory before Microsoft Store release.
- Updated screenshots are retained outside the repository; screenshots, logs, and camera media are not committed.

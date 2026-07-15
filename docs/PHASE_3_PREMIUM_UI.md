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

### Measurement system

The production shell uses a 4-DIP atomic grid and an 8-DIP primary spacing rhythm. Approved spacing steps are 4, 8, 12, 16, 20, 24, 32, 36, and 48 DIPs. `DesignTokens.xaml` is the XAML authority for these values and for semantic uses including:

- `PagePadding` 24 and `CardPadding` 16;
- 16-DIP section gaps and 8-DIP field gaps;
- 12-DIP control and wide-column gaps;
- 32-DIP preview-message padding;
- 16-DIP dialog section gaps and a 24-DIP dialog action gap;
- 12-by-8 status-bar padding and 12-by-4 status-badge padding;
- 8, 12, and 16-DIP corner radii.

The typography scale is 12 DIPs for field/eyebrow labels, 14 for body text, 16 for card headings, 22 for preview and dialog headings, and 24 for the application title. Body and supporting copy use a consistent 20-DIP line height where an explicit line height is required. Segoe UI Variable Text/Display and the existing Segoe UI fallback remain unchanged.

Standard buttons are 40 DIPs high and selectors are 44 DIPs high. Button padding is 16 by 8 DIPs, selector and selector-item padding is 12 by 8 DIPs, standard button minimum width is 88 DIPs, and the primary Start/Stop minimum width is 112 DIPs. Buttons carry no implicit trailing margin: each group declares its own 8- or 12-DIP gap so the last item cannot shift the group's optical center.

`LayoutMetrics` is the managed authority for responsive values used by code-behind. In wide mode the configuration card is `* / 12 / *`, so Device and Format each receive `(usable width - 12) / 2`. Below the single 900-DIP breakpoint, the gap and second star column collapse; Format moves below Device at full width with a 16-DIP vertical gap. The action group similarly moves below its description with a 16-DIP gap. The deterministic policy is narrow at 720 and 899 DIPs and wide at 900 and 1180 DIPs.

Documented measurement exceptions are deliberate rather than spacing drift: the 1- and 2-DIP border/focus strokes are device-pixel visual lines; the 9-by-5 selector chevron and 24-DIP monitor glyph retain their vector geometry; the 20-DIP line height is a typography calculation; and the 1180-by-780 default window, 720-by-620 minimum window, 220-DIP preview minimum, 520-DIP dialog width, 560-DIP preview-copy maximum, and 300-DIP selector-popup maximum are content/work-area constraints. These values remain on the 4-DIP grid where applicable and are represented by named resources.

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

## Geometry and measurement lock — 2026-07-15

- The shell was normalized to the documented 4-DIP atomic grid, 8-DIP primary rhythm, semantic spacing resources, typography scale, 40-DIP buttons, 44-DIP selectors, and 8/12/16-DIP radii.
- Responsive code now uses `LayoutMetrics`; the wide selector columns are mathematically equal around one 12-DIP gap, while narrow selectors and actions stack with exact 16-DIP separation.
- 267 ordinary hardware-independent tests passed, including semantic-token, grid-alignment, shared-control-height, wide-column, breakpoint, preview-star-row, action-ownership, and minimum-width-capacity contracts.
- Release x64 restore/build completed with zero warnings and zero errors.
- Manual dark/light, 720-DIP, normal/maximized, and 100/125/150/200-percent checks confirmed aligned selectors, equal action gaps, centered upcoming labeling, readable text, and a preview-dominant hierarchy.
- HD Camera ready, Previewing, stopped, information-dialog, failure, Stop/release, and close/release paths remained functional. Capture and native-rendering sources were not changed.
- Privacy-safe screenshots remain outside the repository; no screenshot, log, generated package, or captured camera media is committed.

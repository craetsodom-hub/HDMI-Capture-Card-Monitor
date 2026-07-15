# Initial QA Matrix

`A` = automated unit/integration test where hardware is not required. `M` = manual hardware/system test; completion requires actual execution on the named environment.

| Area | Coverage | Method | Expected result |
|---|---|---|---|
| OS | Windows 10; Windows 11 | M | App launches and shuts down cleanly |
| USB | USB 2 cards; USB 3 cards | M | Capabilities and stable preview are correctly reported |
| Resolution/rate | 720p30, 720p60, 1080p30, 1080p60, 1440p exposed, 2160p exposed | M | Supported modes are shown; unsupported modes explain why |
| Formats | MJPEG, YUY2, NV12 | M | Negotiation succeeds or reports precise incompatibility |
| Device topology | Video-only; combined video/audio; multiple devices | M | Correct selection and clear unavailable controls |
| Failure | Disconnect during preview; disconnect during recording; device busy; no HDMI signal; unsupported format | M | Honest status, deterministic cleanup/recovery |
| Permissions | Camera privacy denied; microphone privacy denied | M | Clear actionable failure, no silent fallback |
| Storage/power | Low disk space; sleep/resume | M | Recording/recovery behavior is safe and explicit |
| UI | DPI scaling; keyboard-only navigation; high contrast | M | No clipping, visible focus, accessible state |
| State model | Every valid/invalid lifecycle transition and event | A | Invalid transitions are rejected without mutation or notification; recording returns to preview |
| Logging/settings | Level routing, safe fallback, retention, sensitive-media exclusion, validation | A | Local safe behavior is verified without requiring user directories |

Hardware rows are completed only when the named configuration was genuinely exercised. A webcam validates generic UVC/Media Foundation behavior but does not establish USB HDMI compatibility.

## Phase 1 evidence

| Category | Current evidence | Status |
|---|---|---|
| Automated | Managed device/capability normalization, sorting, rational frame-rate formatting | Executed |
| No-hardware manual | Shell starts, discovery failure is non-fatal, Refresh remains available | Executed on this machine |
| Webcam/manual video-device | HD Camera enumeration and 15 native formats | Executed successfully |
| HDMI capture cards | USB 2 UVC adapter and USB 3 higher-bandwidth card | Outstanding; mandatory before release |

## Phase 2A evidence

| Category | Current evidence | Status |
|---|---|---|
| Automated | 228 ordinary tests: eligibility, cleanup continuation, lifecycle/races, watchdog separation, hidden surface, partial native construction, retry, Fit geometry, DPI conversion, resize coalescing, bounded diagnostics/timing, typed failures, and zero application queue | Executed successfully without hardware |
| HD Camera NV12 | 1280 Ã— 720p Â· 30 fps native input; GPU NV12 output; hardware D3D11 video processor and flip-model HWND swap chain | Executed successfully |
| HD Camera MJPEG | 1280 Ã— 720p Â· 30 fps native compressed input; Media Foundation decoded GPU NV12 output | Executed successfully |
| Repeated lifecycle | 20 confirmed NV12 Start/Stop cycles; immediate Starting placeholder checks; one ambiguous scripted interaction excluded | Executed successfully; no stale-frame flash observed |
| Stress evidence | One genuine no-sample stall and one handled rapid-lifecycle `DXGI_ERROR_DEVICE_REMOVED`; both cleaned up to retryable `DeviceReady` and retry succeeded | Retained as honest driver/system evidence; not part of the continuous passing run |
| Layout/system | Continuous width/height resize, maximize/restore, 12.6-second minimize/restore, WPF placeholder restoration | Executed successfully during one uninterrupted live session |
| Continuous preview | 7,609 received / 7,484 rendered; 125 intentional minimize skips; about 760.9 s; 10.0 received/rendered/sample-timestamp fps; 1.07/1.64 ms processing average/p95; 1.11/1.71 ms sample-return-to-present average/p95; zero presentation failures | Executed successfully |
| Race/manual | Close during Starting in 798 ms; close during Previewing in 649 ms; Windows Camera acquisition after Stop and after application close | Executed successfully; process exited and camera released in both checks |
| Physical USB HDMI | Real HDMI source, capture-card compatibility, disconnect/no-signal semantics, HDMI latency | Outstanding; mandatory before Microsoft Store release |

## Phase 3 evidence

| Category | Current evidence | Status |
|---|---|---|
| Automated presentation | Semantic light/dark resource keys, readable contrast thresholds, system-theme preference resolution, responsive breakpoint behavior, accessibility labels, information-panel content, access-key labels, and disabled future-feature semantics | Executed without hardware |
| Primary workflow | Scan, HD Camera selection, 15-format discovery, 1280 × 720 NV12 Start/Stop/restart, state-aware messages, and live FPS status remain bound to existing Phase 1/2 services and state machine | Executed; stabilized UI diagnostics reported about 10 received/rendered fps |
| Layout | Default, maximized, restored, 11-second minimized/restored, and 720-DIP narrow selector layout; long values use trimming plus full-value tooltips | Executed on the current Windows dark-theme environment |
| Keyboard/accessibility | Access keys for Settings, Help, Device, Format, Refresh, Start/Stop, and dialog close; explicit Automation names and live status regions; visible focus border | Automated metadata checks plus manual keyboard review |
| Theme | Separate semantic light/dark palettes selected from the Windows app preference at startup | Both palettes/contrast automated; active dark theme reviewed manually; light/high-contrast manual review outstanding |
| Informational panels | Settings exposes no fake controls; Help explains the preview path, camera permission, busy-device recovery, and USB HDMI validation boundary | Automated content checks and manual active-preview access-key/focus/Escape review executed |
| Device release | Windows Camera acquisition after Stop and after application close | Executed successfully with HD Camera |
| Physical USB HDMI | HDMI compatibility, no-signal behavior, capture-card disconnect, and HDMI latency | Outstanding; mandatory before Microsoft Store release |

## Phase 4 evidence

| Category | Current evidence | Status |
|---|---|---|
| Automated fullscreen | Eligibility, transition generations, 30 drift-free controller cycles, priority exit, rollback/fallback, exact/maximized placement, signed monitor coordinates, capture/session/HWND continuity, lifecycle ordering, fullscreen layout, and deterministic cursor inactivity | 297 ordinary tests executed successfully without hardware |
| Existing preview architecture | One MainWindow, one HwndPreviewSurface child, one active session, and the Phase 2A renderer remain authoritative; fullscreen has no capture Start/Stop or renegotiation path | Source and unit-test guarded |
| HD Camera fullscreen | Session `4080b0b664ab426086f25400ad21a09a` and child HWND `0xCA0B10` persisted through 30 F11 cycles, 20 rapid F11/Escape alternations, resize/maximize restore, minimize, and a 613.3-second fullscreen run; 10.0 received/rendered fps afterward; no warnings, presentation failures, or D3D removal | Executed successfully with 1280 × 720 NV12; no 30-fps delivery claim |
| Placement and timing | Resized native bounds `130,66–1143,846` restored exactly; maximized `showCmd=3` and the same normal bounds restored; 108 logged transitions ranged 43.5–301.4 ms, average 74.5 ms | Executed successfully without visible drift or off-screen placement |
| Cursor and lifecycle | Cursor hidden after inactivity (`CURSOR_SHOWING` clear), restored on exit; Alt+Tab and 10.5-second minimize/restore recovered to 10.0/10.0 fps; close from fullscreen restored in 99.5 ms and completed shutdown | Executed successfully; automatic Stop/failure order remains deterministic-test evidence because fullscreen intentionally exposes no overlaid Stop control |
| Device release | Windows Camera acquired HD Camera after Stop and after fullscreen application close; close diagnostics recorded 1,456 received / 1,456 rendered and zero presentation failures | Executed successfully |
| Display and theme | Dark/light themes and live fullscreen at 100%, 150%, and 200% scaling | Executed successfully on one 2160 × 1440 display; secondary and negative-coordinate physical-monitor validation unavailable |
| Safely induced failures | Fullscreen rollback, safe fallback, runtime preview failure, Stop, display-change, and disposal-during-entry | Deterministic tests executed; no real preview failure or monitor removal was deliberately forced |
| Physical USB HDMI | HDMI compatibility, no-signal behavior, disconnect semantics, and HDMI latency | Outstanding; mandatory before Microsoft Store release |

## Phase 5 evidence

| Category | Current evidence | Status |
|---|---|---|
| Automated audio core | 411 ordinary tests covering endpoint safety, native silent/null-pointer handling, guaranteed capture/render release, fresh-client fallback, complete-frame ring-buffer behavior, gain/mute ramps, observer isolation, bounded diagnostics publishing, unified worker lifetime/timeouts, lifecycle ordering, responsive slider/theme semantics, and repeated disposal | Executed successfully without hardware |
| Endpoint discovery | Active Realtek microphone and speaker endpoints enumerated with safe names; opaque IDs were not displayed or logged; system-default output remains an explicit choice | Executed successfully on this Windows machine |
| Initialization and format | Hybrid `IAudioClient3` capture/classic shared render with `RATEADJUST`; 32-bit IEEE float, 48 kHz, 2 channels; 480-frame capture/render periods; 1,056/2,238-frame native buffers | Executed successfully in a muted hardware session |
| Buffer and drift policy | 960-frame target, 2,880-frame hard capacity, complete frames only, one-period render writes, capture-before-render combined wakes, and a non-real-time proportional controller bounded to ±3,000 ppm | Source, deterministic-test, and hardware-run guarded |
| Historical ten-minute baseline | 28,692,672 captured; 28,791,552 rendered; 100,512 silent; 208 underruns; 1 overrun; 96 dropped | Superseded evidence that justified the bounded correction; not presented as a pass |
| Latest ten-minute muted run | 28,766,592 captured; 28,769,760 rendered; 4,608 silent; one 9-underrun burst observed at 98.060-98.565 s; 0 overruns; 0 dropped; discontinuity count 3 observed at 1.438 s and count 5 at 51.549 s; queue range 0-2,016 frames; adjustment range -3,000 to +3,000 ppm | Cleanup completed, but the strict hardware assertion failed; retained as blocking evidence rather than a lossless claim |
| Repeated lifecycle | Twenty muted Start/Stop cycles completed and capture/render endpoints were released after every cycle | Executed successfully |
| Fullscreen and video ordering | Audio ownership is independent of presentation state; first real video frame precedes audio start; audio stops before video cleanup | Deterministic lifecycle tests; audible live fullscreen validation remains outstanding |
| Audible quality | Only open laptop speakers were available; unmuted microphone-to-speaker monitoring was intentionally not attempted because of feedback risk | Blocked; no sound, crackle, gap, channel, feedback, drift, pitch, fullscreen, minimize/restore, or quality claim |
| Physical USB HDMI | Video/audio endpoint association, HDMI audio format behavior, A/V synchronization, feedback-safe audible monitoring, no-signal/disconnect behavior, and HDMI latency | Outstanding; mandatory before Microsoft Store release |

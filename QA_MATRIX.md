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
| Automated | Eligibility, lifecycle/races, surface visibility, Fit geometry, DPI conversion, resize coalescing, bounded diagnostics/timing, typed failures, and zero application queue | Executed without hardware |
| HD Camera NV12 | 1280 Ã— 720p Â· 30 fps native input; GPU NV12 output; hardware D3D11 video processor and flip-model HWND swap chain | Executed successfully |
| HD Camera MJPEG | 1280 Ã— 720p Â· 30 fps native compressed input; Media Foundation decoded GPU NV12 output | Executed successfully |
| Repeated lifecycle | 20 paced NV12 Start/Stop cycles after correcting WPF/native HWND activation ordering | Executed successfully; camera released after each stop |
| Aggressive lifecycle evidence | Rapid churn before the activation-order correction produced two handled `DXGI_ERROR_DEVICE_REMOVED` events | Retained as corrective evidence; not counted as the passing cycle run |
| Layout/system | Resize, maximize, minimize/restore, WPF placeholder restoration | Executed successfully during live HD Camera preview |
| Continuous preview | 613.5-second HD Camera NV12 session; 9,666/9,666 frames; 15.0 final rolling FPS; 1.27 ms average and 1.85 ms approximate p95 processing; zero presentation failures | Executed successfully |
| Race/manual | Automated Stop/disposal-during-Starting coverage; active-preview close in 0.42 s; app reopen; Windows Camera acquisition after normal Stop and after active-preview close | Executed successfully; camera released in both hardware checks |
| Physical USB HDMI | Real HDMI source, capture-card compatibility, disconnect/no-signal semantics, HDMI latency | Outstanding; mandatory before Microsoft Store release |

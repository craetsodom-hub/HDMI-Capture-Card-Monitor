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
| State model | Valid/invalid lifecycle transitions and events | A | Invalid transitions are rejected without mutation |
| Logging/settings | Level routing, sensitive-media exclusion, validation | A | Local safe behavior is verified |

Phase 0 has only the state-model automated coverage; every hardware item remains unexecuted until its relevant phase.

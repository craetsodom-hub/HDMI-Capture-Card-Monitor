# Phase 5: Low-Latency Audio Monitoring

## Product rationale and scope

Local audio monitoring lets the application behave like a monitor without creating a recording. It is deliberately optional because some capture cards expose no audio endpoint, customers may prefer another playback route, and monitoring a microphone through speakers can cause feedback. `No audio` is the default honest choice. The customer can select a capture endpoint, choose `System default output` or a named active render endpoint, mute locally, and set an application-only monitoring volume.

Phase 5 does not record audio, mix audio into video, control the system endpoint volume, infer that a separately enumerated endpoint belongs to a video device, or claim A/V synchronization. Snapshot and Record remain disabled. Preview acquisition, the D3D11 renderer, the preview child HWND, and fullscreen presentation are unchanged.

## Endpoint discovery and identity

`CoreAudioEndpointDiscoveryService` uses MMDevice to enumerate active `eCapture` and `eRender` endpoints and to resolve the default multimedia render endpoint. Its managed models contain a customer-safe friendly name plus optional device description, interface-friendly name, and container identifier. Native endpoint IDs are retained only as opaque activation keys. They are not parsed, synthesized, displayed, logged, or returned by `ToString`. Missing names receive generic labels, duplicate names are disambiguated without exposing IDs, malformed entries are skipped, and each property store, collection, device, and allocated `PROPVARIANT` is released on every path.

The UI does not invent endpoints. `No audio` means no audio session will be created. `System default output` means the current default multimedia render endpoint will be resolved when monitoring starts.

## WASAPI shared-mode session

`WasapiAudioMonitorService` serializes start, stop, and disposal and owns at most one `WasapiAudioMonitorSession`. The session creates one named background MTA thread; that thread owns every capture/render COM object and event handle for its lifetime. It activates the requested input and render devices, creates `IAudioClient3` clients where available, and prefers shared-mode engine-period initialization. If the AudioClient3 period path is unavailable or rejected, it falls back explicitly to classic event-driven `IAudioClient.Initialize` in shared mode. The selected initialization path is diagnostic data; there is no silent fallback to an unrelated endpoint.

The render endpoint's shared mix format defines a common application format. Windows Audio Engine conversion supplies the capture stream in matching 32-bit IEEE-float sample rate, channel count, and channel mask. The application contains no custom resampler, codec, channel mixer, or third-party native audio runtime.

## Event loop, scheduling, and buffering

The worker waits on stop, capture, and render events. It drains complete capture packets through `IAudioCaptureClient`, preserves silent/discontinuity/timestamp flags as counters, and releases every native packet before continuing. Render events request exactly one period through `IAudioRenderClient`. The worker registers with MMCSS under the `Audio` task and requests high relative priority; failure to register is observable in diagnostics rather than fatal.

Samples cross a preallocated, interleaved-float ring buffer that accepts and returns complete audio frames only. The current observed 48 kHz stereo policy is:

- Capture period: 480 frames / 10 ms.
- Render period: 480 frames / 10 ms.
- Startup and operational target: 1,440 frames / 30 ms (three periods).
- Hard capacity: 2,880 frames / 60 ms (six periods).
- Render request: one period per render event.

If insufficient frames are available, the unwritten render region is silent and an underrun is counted; the thread never blocks waiting for capture. If capacity would be exceeded, the oldest complete frames are discarded and overrun/drop counters are advanced. No queue-growth policy may exceed six periods. Queue milliseconds describe application buffer occupancy only; they are not camera-to-speaker, HDMI, or end-to-end latency.

## Volume and mute

Volume is a bounded application gain, not the Windows endpoint volume. Each rendered buffer applies a short linear ramp from the previous gain to the requested gain. Muting ramps toward zero and unmuting ramps back to the selected volume, preventing a single-sample step. Gain, mute, ring-buffer math, and frame alignment are deterministic managed code and are unit tested without hardware.

## Video lifecycle and fullscreen continuity

Video remains authoritative for the customer workflow. Device and audio endpoint discovery can run together, but monitoring starts only after the first real video frame has successfully presented. An audio startup or runtime failure is reported as an audio-specific customer message and does not tear down a healthy video preview. Stop, preview failure, close, and application disposal signal and join audio before video cleanup. A stale audio event is ignored unless its session ID matches the current session.

Fullscreen is presentation-only. Entering or leaving fullscreen does not restart audio, video, Media Foundation, D3D11, the swap chain, or the preview HWND. Stop and close still request fullscreen exit first, then audio cleanup, then video cleanup.

## Failures, diagnostics, and cleanup

Customer-facing failures distinguish access denied, missing capture/output endpoints, device in use, unsupported format, stopped audio service, endpoint creation, device/resources invalidation, buffer failure, startup timeout, stop timeout, processing failure, and an otherwise safe generic failure. Messages and logs avoid opaque IDs and media content.

Diagnostics are emitted at most twice per second and include safe endpoint names, advertised capture format, render mix format, common format, AudioClient3/classic path, capture/render periods and native buffer sizes, current/maximum/capacity/target queue frames, captured/rendered/silent frames, discontinuities, timestamp errors, underruns, overruns, dropped frames, device/QPC positions, MMCSS state, volume/mute, last packet/render times, and the latest failure category.

Stop is idempotent, signals the worker, and waits at most three seconds. Native objects remain owned by the worker until it exits. Cleanup attempts reverse-order release of render/capture services, clients, devices, event handles, MMCSS registration, COM initialization, and the ring buffer even if an earlier release fails. Application shutdown disposes the audio monitor and endpoint discovery before the Media Foundation video services.

## Executed validation and honest result

This machine exposed `Réseau de microphones (Realtek(R) Audio)` and `Haut-parleurs (Realtek(R) Audio)`. Endpoint discovery used their safe names; no opaque identifier was displayed. The physical path was tested muted at zero gain to prevent acoustic feedback. `IAudioClient3` initialized shared 32-bit float audio at 48 kHz stereo with 480-frame capture/render periods and 1,056-frame native buffers.

A genuine ten-minute muted run completed cleanup and recorded:

- 28,692,672 captured frames and 28,791,552 rendered frames.
- 100,512 silent frames.
- 208 underruns, 1 overrun, and 96 dropped frames.
- 5 discontinuities and 0 timestamp errors.
- Maximum observed application queue: 1,920 frames (40 ms).

Corrective bounded policies were then compared without adding a custom resampler, hiding clock adjustment, or exceeding the six-period limit. The final one-minute observation recorded 2,861,472 captured frames, 2,866,560 rendered frames, 6,624 silent frames, 13 underruns, 0 overruns, 0 dropped frames, 5 discontinuities, 0 timestamp errors, and a maximum queue of 2,496 frames (52 ms). Twenty additional muted Start/Stop cycles completed, and both endpoints were released after every cycle.

Recurring underruns therefore remain a Phase 5 blocker. The implementation and draft PR do not claim stable, crackle-free, drift-free, or low-latency audible monitoring. No wired headphones were present, so an unmuted microphone test was intentionally omitted; using laptop speakers would create a feedback risk. Audible quality, fullscreen audible continuity, long-duration clock drift, and A/V synchronization are unvalidated.

## Mandatory remaining validation and deferred features

Before Microsoft Store release, testing must include a physical USB HDMI capture card and source, its actual audio endpoint/topology, explicit endpoint association behavior, supported audio formats, no-signal and disconnect behavior, safe headphone monitoring, long-duration queue/drift behavior, A/V synchronization, and measured HDMI audio/video latency. A webcam and laptop microphone do not establish HDMI compatibility.

Audio recording, media muxing, snapshots, reconnect, diagnostics export, system-volume control, custom resampling, and automatic audio/video endpoint association remain intentionally deferred. The Phase 5 pull request stays draft and unmerged while the recurring-underrun blocker and physical-hardware gaps remain open.

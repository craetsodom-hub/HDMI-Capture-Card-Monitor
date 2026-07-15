# Phase 5: Low-Latency Audio Monitoring

## Product rationale and scope

Local audio monitoring lets the application behave like a monitor without creating a recording. It is deliberately optional because some capture cards expose no audio endpoint, customers may prefer another playback route, and monitoring a microphone through speakers can cause feedback. `No audio` is the default honest choice. The customer can select a capture endpoint, choose `System default output` or a named active render endpoint, mute locally, and set an application-only monitoring volume.

Phase 5 does not record audio, mix audio into video, control the system endpoint volume, infer that a separately enumerated endpoint belongs to a video device, or claim A/V synchronization. Snapshot and Record remain disabled. Preview acquisition, the D3D11 renderer, the preview child HWND, and fullscreen presentation are unchanged.

## Endpoint discovery and identity

`CoreAudioEndpointDiscoveryService` uses MMDevice to enumerate active `eCapture` and `eRender` endpoints and to resolve the default multimedia render endpoint. Its managed models contain a customer-safe friendly name plus optional device description, interface-friendly name, and container identifier. Native endpoint IDs are retained only as opaque activation keys. They are not parsed, synthesized, displayed, logged, or returned by `ToString`. Missing names receive generic labels, duplicate names are disambiguated without exposing IDs, malformed entries are skipped, and each property store, collection, device, and allocated `PROPVARIANT` is released on every path.

The UI does not invent endpoints. `No audio` means no audio session will be created. `System default output` means the current default multimedia render endpoint will be resolved when monitoring starts.

## WASAPI shared-mode session

`WasapiAudioMonitorService` serializes start, stop, and disposal and owns at most one `WasapiAudioMonitorSession`. The session owns a named background MTA packet worker, a below-normal MTA queue-rate controller, and a bounded managed diagnostics publisher. Completion means all three have settled. The preferred path uses `IAudioClient3.InitializeSharedAudioStream` for capture and classic shared `IAudioClient.Initialize` with `RATEADJUST` for render. The actual render engine period is queried rather than inferred. This hybrid is deliberate because `InitializeSharedAudioStream` supports `EVENTCALLBACK` but not `RATEADJUST`. Each attempt activates a fresh capture/render client pair. If hybrid initialization fails for an eligible reason, the complete partial pair is released before a fresh classic pair is activated; no `IAudioClient` is initialized twice. Device invalidation, access denial, and an unavailable audio service retain their precise failure categories. The selected initialization path is diagnostic data; there is no silent fallback to an unrelated endpoint.

The render endpoint's shared mix format defines a common application format. Windows Audio Engine conversion supplies the capture stream in matching 32-bit IEEE-float sample rate, channel count, and channel mask. The application contains no custom resampler, codec, channel mixer, or third-party native audio runtime.

## Event loop, scheduling, and buffering

The worker waits on stop, capture, and render events. It drains complete capture packets through `IAudioCaptureClient`, preserves silent/discontinuity/timestamp flags as counters, and releases every successful nonzero native acquisition exactly once in a guaranteed `finally`. A silent packet never reads or spans its native pointer and writes the exact frame count as zeros. A non-silent nonzero packet requires a valid pointer and checked complete-sample sizing. Render events drain any already-available capture packet before requesting exactly one render period, preventing a ready packet from becoming a false underrun because the render handle has the lower wait index. Initial prefill and normal render processing also release every acquired packet exactly once; any post-acquisition failure releases it as silence before propagating a typed failure. Capture-only wakes do not render early. The worker registers with MMCSS under the `Audio` task and requests high relative priority; failure to register is observable in diagnostics rather than fatal.

Samples cross a preallocated, interleaved-float ring buffer that accepts and returns complete audio frames only. The current observed 48 kHz stereo policy is:

- Capture period: 480 frames / 10 ms.
- Render period: 480 frames / 10 ms.
- Startup and operational target: 960 frames / 20 ms (two periods).
- Hard capacity: 2,880 frames / 60 ms (six periods).
- Render request: one period per render event.

If insufficient frames are available, the unwritten render region is silent and an underrun is counted; the thread never blocks waiting for capture. If capacity would be exceeded, the oldest complete frames are discarded and overrun/drop counters are advanced. No queue-growth policy may exceed six periods. Queue milliseconds describe application buffer occupancy only; they are not camera-to-speaker, HDMI, or end-to-end latency.

Independent capture and render clocks are reconciled by a small queue controller on a separate below-normal-priority MTA thread. It owns `IAudioClockAdjustment`, samples queue depth every 500 ms, and applies a proportional correction centered on the two-period target, clamped to ±3,000 ppm. `SetSampleRate` never runs on the MMCSS packet thread. The applied correction is recorded in diagnostics, and the controller is joined before its render client can be released.

## Volume and mute

Volume is a bounded application gain, not the Windows endpoint volume. Each rendered buffer applies a short linear ramp from the previous gain to the requested gain. Muting ramps toward zero and unmuting ramps back to the selected volume, preventing a single-sample step. Gain, mute, ring-buffer math, and frame alignment are deterministic managed code and are unit tested without hardware.

The volume control uses the shared dark/light theme dictionaries, a 40-DIP practical hit area, a restrained track with accent fill, a clear 16-DIP thumb, visible hover/drag/keyboard-focus states, and an explicit disabled treatment. When `No audio` is selected, the mute button, slider, and percentage text all read as unavailable. Native `Slider` commands preserve arrow-key and PageUp/PageDown behavior, and the existing automation name remains exposed. The audio row keeps the established spacing/radius tokens and does not expand at the expense of preview dominance.

## Video lifecycle and fullscreen continuity

Video remains authoritative for the customer workflow. Device and audio endpoint discovery can run together, but monitoring starts only after the first real video frame has successfully presented. An audio startup or runtime failure is reported as an audio-specific customer message and does not tear down a healthy video preview. Stop, preview failure, close, and application disposal signal and join audio before video cleanup. A stale audio event is ignored unless its session ID matches the current session.

Fullscreen is presentation-only. Entering or leaving fullscreen does not restart audio, video, Media Foundation, D3D11, the swap chain, or the preview HWND. Stop and close still request fullscreen exit first, then audio cleanup, then video cleanup.

## Failures, diagnostics, and cleanup

Customer-facing failures distinguish access denied, missing capture/output endpoints, device in use, unsupported format, stopped audio service, endpoint creation, device/resources invalidation, buffer failure, startup timeout, stop timeout, processing failure, and an otherwise safe generic failure. Messages and logs avoid opaque IDs and media content.

Startup failure displays `Video is live. Audio monitoring could not start.` A later processing failure displays `Video is live. Audio monitoring stopped.` Device/resources invalidation displays `Video is live. The selected audio device disconnected.` All three leave healthy video in `Previewing` and keep fullscreen presentation usable.

The MMCSS packet worker only replaces the latest snapshot in a capacity-one publisher. Its below-normal managed worker invokes UI-facing subscribers at most twice per second, contains every subscriber independently, and is included in session shutdown. State and failure events use the same per-subscriber isolation, so a bad observer cannot terminate the packet worker. Diagnostics include safe endpoint names, advertised capture format, render mix format, common format, initialization path, capture/render periods and native buffer sizes, current/maximum/capacity/target queue frames, captured/rendered/silent frames, discontinuities, timestamp errors, underruns, overruns, dropped frames, applied rate correction, device/QPC positions, MMCSS state, volume/mute, last packet/render times, and the latest failure category.

Stop is idempotent, signals the packet worker, and waits at most three seconds. Cleanup isolates controller stop, native client stops, every COM release, each handle disposal, format-memory release, MMCSS revert, COM uninitialization, and logging, so one failure cannot prevent later cleanup. Completion signalling is in the outermost guaranteed path. If the queue controller misses its bound, the session reports `StopTimeout`, remains the service's active session, blocks another start, retains the render client, and waits asynchronously; ownership is released and completion is signalled only after the controller eventually settles. Application shutdown disposes the audio monitor and endpoint discovery before the Media Foundation video services.

## Executed validation and honest result

This machine exposed `Réseau de microphones (Realtek(R) Audio)` and `Haut-parleurs (Realtek(R) Audio)`. Endpoint discovery used their safe names; no opaque identifier was displayed. The physical path was tested muted at zero gain to prevent acoustic feedback. The hybrid path initialized shared 32-bit float audio at 48 kHz stereo with 480-frame capture/render periods and 1,056/2,238-frame native buffers.

A historical ten-minute baseline recorded 208 underruns, 1 overrun, and 96 dropped frames. A later run recorded zero loss, but it predates the final native-safety correction and is not used as the current result. The final correction first exposed 3 underruns because render was serviced before an already-ready capture packet. Reversing that combined-wake order produced a passing two-minute smoke run with zero loss. The decisive new ten-minute muted run and cleanup then recorded:

- 28,766,592 captured frames and 28,769,760 rendered frames.
- 4,608 silent frames.
- 9 underruns in one observed burst: count 7 at 98.060 seconds and count 9 at 98.565 seconds.
- 0 overruns and 0 dropped frames.
- 5 capture discontinuity flags and 0 timestamp errors. The bounded publisher first observed count 3 at 1.438 seconds and count 5 at 51.549 seconds; the later underrun burst did not coincide with a newly observed discontinuity flag.
- Queue-depth range 0 through 2,016 frames; final queue 960 frames.
- Applied rate-adjustment range -3,000 through +3,000 ppm; final adjustment 0 ppm.

The session stopped and released ownership despite the strict hardware assertion failing on the nonzero underrun count. Twenty additional muted Start/Stop cycles then completed successfully, reacquiring and releasing the capture/render endpoints every time. The 20 ms target and six-period hard capacity were preserved; the evidence is not hidden by weakening the assertion or inflating latency outside this correction.

Only the open laptop speakers were available; no wired or USB headphones/earphones were present. An unmuted microphone-to-speaker test was intentionally omitted because of feedback risk. Audible sound, crackling, gaps, channel balance, feedback, pitch/drift, continuous volume changes, mute cycles, fullscreen listening, minimize/restore listening, and audible ten-minute quality therefore remain blocked and unclaimed. PR #6 remains draft.

## Mandatory remaining validation and deferred features

Before Microsoft Store release, testing must include a physical USB HDMI capture card and source, its actual audio endpoint/topology, explicit endpoint association behavior, supported audio formats, no-signal and disconnect behavior, safe headphone monitoring, long-duration queue/drift behavior, A/V synchronization, and measured HDMI audio/video latency. A webcam and laptop microphone do not establish HDMI compatibility.

Audio recording, media muxing, snapshots, reconnect, diagnostics export, system-volume control, custom resampling, and automatic audio/video endpoint association remain intentionally deferred. The Phase 5 pull request stays draft and unmerged while audible headphone and physical USB HDMI hardware gaps remain open.

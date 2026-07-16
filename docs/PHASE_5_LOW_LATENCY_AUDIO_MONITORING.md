# Phase 5: Low-Latency Audio Monitoring

## Product scope

Local audio monitoring is optional. `No audio` is the honest default; the customer may instead select an active capture endpoint, use `System default output` or a named render endpoint, mute locally, and set an application-only monitoring volume. The application does not infer that an audio endpoint belongs to a video device.

Phase 5 does not record or mux audio, control system endpoint volume, claim A/V synchronization, or implement custom resampling. Snapshot and Record remain disabled. Video acquisition, the D3D11 renderer, the preview child HWND, diagnostics, and fullscreen presentation are unchanged.

## Endpoint discovery and identity

`CoreAudioEndpointDiscoveryService` uses MMDevice to enumerate active capture and render endpoints and resolve the default multimedia output. Managed models expose customer-safe names; native IDs remain opaque activation keys and are neither displayed nor logged. Duplicate and missing names are handled without exposing IDs, malformed entries are skipped, and every native property, collection, and device is released on all paths.

## WASAPI session and ownership

`WasapiAudioMonitorService` serializes start, stop, and disposal and owns at most one `WasapiAudioMonitorSession`. A session owns four workers:

- One named MMCSS MTA capture/render packet worker.
- One below-normal MTA queue-rate controller.
- One below-normal bounded control-event publisher.
- One below-normal capacity-one diagnostics publisher.

Session completion means all workers, wait handles, and retained COM ownership have settled. The preferred initialization uses `IAudioClient3.InitializeSharedAudioStream` for capture and classic shared `IAudioClient.Initialize` with `RATEADJUST` for render. The render engine period is queried. Each initialization attempt activates a fresh client pair; an eligible hybrid failure releases the complete partial pair before activating a fresh classic pair. No `IAudioClient` is initialized twice, and there is no fallback to an unrelated endpoint.

The render mix format defines the common 32-bit IEEE-float application format. Windows Audio Engine performs required shared-mode conversion. The application contains no custom resampler, codec, channel mixer, or third-party native audio runtime.

## Packet loop and bounded buffering

The packet worker waits on stop, render, and capture events. A render wake first drains every capture packet already available, then requests exactly one render period; a capture-only wake does not render early. Every successful nonzero native acquisition is released exactly once in a guaranteed `finally`. Silent packets do not dereference their pointer and write the exact frame count as zeros. Post-acquisition render failures release the buffer as silence before propagating a typed failure.

Samples cross a preallocated complete-frame float ring. The observed 48 kHz stereo path has 480-frame capture/render periods. Candidate queue targets are two periods (960 frames, approximately 20 ms) and three periods (1,440 frames, approximately 30 ms). Capacity is hard-bounded to six periods (2,880 frames, approximately 60 ms). Queue time is application buffering only; it is not HDMI, A/V, camera-to-speaker, or end-to-end latency.

Insufficient frames produce silence and increment an underrun. Capacity pressure discards the oldest complete frames and increments overrun/drop counters. Neither counter is suppressed, and capacity is not increased to hide instability.

## Damped queue-rate controller

`AudioQueueRateControllerPolicy` uses an exponential filtered queue error, a 0.25-period deadband, a 400-ppm-per-second slew limit, and a default maximum correction of +/-1,000 ppm. It cannot jump directly across polarity. Requested and applied correction, saturation duration, and nonzero direction-change count are recorded separately. `IAudioClockAdjustment.SetSampleRate` executes only on the below-normal controller thread, never on the MMCSS packet worker. Shutdown restores nominal sample rate before releasing ownership.

The policy is deterministic and tested for stable target, deadband noise, sustained positive/negative drift, scheduling disturbance, slew limiting, bounded correction, polarity behavior, and return to nominal. The ring remains bounded regardless of controller state.

## Non-real-time event delivery

Diagnostics use a capacity-one latest-value publisher and are delivered at most twice per second. State and failure notifications share a separate bounded eight-entry control publisher. Retained events keep their order, redundant identical pending state notifications are coalesced, and state entries yield queue capacity to a failure. Subscriber invocation is independent and exception-contained. A slow, blocked, or throwing observer never executes on or delays the MMCSS packet worker.

Both publishers drain or settle before session completion. If synchronous Stop times out, each worker's eventual `finally` disposes its events exactly once after use; no external second Stop is required. The same eventual path applies to the rate controller and its retained render client.

Diagnostics include safe endpoint names, formats, initialization path, native periods/buffers, current/average/maximum/capacity/target queue frames, capture/render/silence/loss counters, requested/applied correction, saturation, direction changes, device/QPC positions, MMCSS state, volume/mute, and last packet/render times. Every capture discontinuity retains monotonic time, packet frames, position deltas, queue before/after, controller values, lifecycle phase, and whether a loss counter changed within the next 100 ms. A later unrelated loss is not retroactively attributed.

## Volume, video, fullscreen, and failures

Volume is bounded application gain with a short linear ramp. Mute also ramps rather than stepping. The themed slider preserves native keyboard and automation behavior and becomes unavailable with `No audio`.

Audio starts only after the first real video frame presents. Audio failure does not fault video, stop fullscreen, restart preview, reset video diagnostics, or hide the preview HWND. Fullscreen continues to reuse the same MainWindow, capture session, D3D11 renderer, swap chain, and child HWND.

Required customer wording is preserved:

- Startup: `Video is live. Audio monitoring could not start.`
- Runtime: `Video is live. Audio monitoring stopped.`
- Disconnected: `Video is live. The selected audio device disconnected.`

## Final stability-gate evidence

The available endpoints were the safe-name Realtek microphone array and Realtek laptop speakers. Testing remained muted at zero gain to prevent acoustic feedback. The production hybrid path negotiated shared 32-bit float, 48 kHz stereo with 480-frame capture/render periods and 1,056/2,238-frame native buffers.

Neither permitted queue target passed the strict loss-free gate:

- Two-period, 600 seconds: 196 underruns, 0 overruns, 0 dropped frames; observed queue min/average/max 0/646.7/2,112 frames; requested and applied correction -591.9 to +561.0 ppm; 0 ms saturation; 1 polarity change. Six discontinuity flags were recorded, including a steady-state pair near 50.29 seconds. Stop completed and endpoints released.
- Three-period, 600 seconds: 64 underruns, 3 overruns, 288 dropped frames; observed queue min/average/max 0/1,698.4/2,496 frames; requested and applied correction -1,000.0 to +944.8 ppm; 48,000 ms saturation; 3 polarity changes. Nine discontinuity flags were recorded, including steady-state observations near 50.23, 240.16, and 342.69 seconds. Stop completed and endpoints released.
- Classic shared diagnostic, three-period, 120 seconds: 139 underruns, 0 overruns, 0 drops, with a prolonged capture-starvation burst. This shows the instability is not isolated to the preferred hybrid initializer.
- Final corrected-telemetry diagnostic, two-period, 120 seconds: 137 underruns, 0 overruns, 0 drops; queue min/average/max 0/649.5/2,016 frames; requested/applied correction -870.7 to +681.5 ppm; 0 ms saturation; 1 polarity change. The bounded 100 ms correlation marked the 56.726-second steady-state discontinuity as followed by immediate loss and correctly left the 100.200-second observation unassociated with later loss.

Because no candidate achieved zero steady-state loss, no stable queue target is selected. The two-period production default remains a candidate, not an approved stability result. No queue was increased beyond three target periods or six periods of capacity, and no failed assertion was weakened. The required three successful ten-minute sessions were therefore not obtainable on this hardware.

Twenty additional muted Start/Stop cycles passed in 35 seconds. Every cycle reacquired, stopped, and released the capture/render endpoints, and a subsequent endpoint enumeration remained successful.

The laptop exposed only its open speakers; no wired or USB headphones/earphones were connected. Unmuted monitoring was intentionally not attempted because microphone-to-speaker feedback would violate the safety boundary. Audible sound, routing, crackling, gaps, pitch/warble, mute cycles, volume travel, fullscreen/minimize behavior, ten-minute listening, and close/reacquisition checks remain blocked and unclaimed. PR #6 therefore remains draft and Phase 5 is not complete.

## Mandatory remaining validation

Before merge and Microsoft Store release, Phase 5 still requires isolated wired/USB headphone validation, three passing ten-minute muted sessions (or one passing thirty-minute session), and physical USB HDMI capture-card audio testing. HDMI endpoint topology, format support, disconnect/no-signal behavior, A/V synchronization, and measured HDMI latency remain unknown. A laptop microphone does not establish HDMI compatibility.

Audio recording, muxing, Snapshot, reconnect, diagnostics export, system-volume control, and automatic audio/video endpoint association remain intentionally deferred.

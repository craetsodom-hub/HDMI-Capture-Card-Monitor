# Phase 5: Low-Latency Audio Monitoring

## Product scope

Local audio monitoring is optional. `No audio` is the honest default; the customer may instead select an active capture endpoint, use `System default output` or a named render endpoint, mute locally, and set an application-only monitoring volume. The application does not infer that an audio endpoint belongs to a video device.

Phase 5 does not record or mux audio, control system endpoint volume, claim A/V synchronization, or implement custom resampling. Snapshot and Record remain disabled. Video acquisition, the D3D11 renderer, the preview child HWND, diagnostics, and fullscreen presentation are unchanged.

## Endpoint discovery and identity

`CoreAudioEndpointDiscoveryService` uses MMDevice to enumerate active capture and render endpoints and resolve the default multimedia output. Managed models expose customer-safe names; native IDs remain opaque activation keys and are neither displayed nor logged. Duplicate and missing names are handled without exposing IDs, malformed entries are skipped, and every native property, collection, and device is released on all paths.

## WASAPI session and ownership

`WasapiAudioMonitorService` serializes start, stop, and disposal and owns at most one `WasapiAudioMonitorSession`. A normal session has a lightweight MTA supervisor and two independent packet threads:

- One named MTA capture thread registered independently with MMCSS `Audio`. It waits only for its capture event and the shared stop event, drains every available packet, and never logs or calls customer-facing subscribers.
- One named MTA render thread registered independently with MMCSS `Audio`. It waits only for its render event and the shared stop event, fills all currently available native frames, and never waits for capture, logs, or calls customer-facing subscribers.
- One normal-priority MTA supervisor that owns initialization order, preroll barriers, state/failure coordination, stop signalling, worker completion, diagnostics snapshots, and final COM/handle release.
- One below-normal rate-controller thread only when evidence-driven adjustment is enabled, one bounded control-event publisher, and one capacity-one diagnostics publisher.

Session completion means both packet threads, the optional controller, both publishers, all wait handles, and retained COM ownership have settled. The public stop result remains bounded to three seconds; a timeout retains ownership, rejects restart, and releases clients only after the delayed worker really exits. Capture/render COM services are not released while either packet thread can still call them, and every thread balances its own MTA initialization. The preferred initialization uses `IAudioClient3.InitializeSharedAudioStream` for capture and classic shared `IAudioClient.Initialize` with `RATEADJUST` for render. The render engine period is queried. Each initialization attempt activates a fresh client pair; an eligible hybrid failure releases the complete partial pair before activating a fresh classic pair. No `IAudioClient` is initialized twice, and there is no fallback to an unrelated endpoint.

The render mix format defines the common 32-bit IEEE-float application format. Windows Audio Engine performs required shared-mode conversion. The application contains no custom resampler, codec, channel mixer, or third-party native audio runtime.

## Packet processing and bounded SPSC buffering

Every successful nonzero native acquisition is released exactly once in a guaranteed `finally`. Silent capture packets do not dereference their pointer and write the exact frame count as zeros. On each render event the render thread reads padding once, calculates `renderBufferFrames - padding`, acquires that complete available span, reads as many live frames as exist, clears only the missing tail, applies gain over the whole acquired packet, and releases exactly that frame count once. It is not capped to one nominal period, so a late wake can recover multiple periods in one write. Post-acquisition failures still release the buffer safely before propagating a typed failure.

Samples cross a preallocated lock-free single-producer/single-consumer float ring. Capture is the sole producer and render is the sole consumer. Monotonic frame sequences publish producer writes and consumer release with `Volatile.Write`; the opposite side observes them with `Volatile.Read`, providing release/acquire ordering without a normal-path lock or packet allocation. Copy and wrap calculations are checked and operate only on complete interleaved frames. The producer never overwrites storage still visible to the consumer. Physical-full rejection counts incoming complete frames separately; consumer-owned high-watermark trimming skips only the oldest complete frames and has a separate counter. Diagnostics take a coherent sequence snapshot without blocking either packet thread.

The observed 48 kHz stereo path has 480-frame capture/render periods. Candidate queue targets remain two periods (960 frames, approximately 20 ms) and three periods (1,440 frames, approximately 30 ms); capacity remains hard-bounded to six periods (2,880 frames, approximately 60 ms). Queue time is application buffering only. It is not HDMI, A/V, camera-to-speaker, or end-to-end latency.

Startup explicitly starts capture first, accumulates the selected target, freezes capture draining at that preroll barrier, pre-fills the actual 2,238-frame native render buffer with deliberate silence, starts render, then releases capture and enters `Monitoring` only after both packet threads reported ready. Deliberate native prefill is recorded as 2,238 startup-silence frames and is not a ring starvation. The known quantities are 46.625 ms of native render buffer plus a 20 ms or 30 ms application target (3,198 or 3,678 total known frames respectively); their sum is not an end-to-end latency measurement.

## Evidence-driven queue-rate controller

Hardware baselines can explicitly disable rate adjustment. When enabled, `AudioQueueRateControllerPolicy` first observes a stable initial window and derives drift from cumulative captured and rendered frame counts, not a single queue sample. It requires sustained drift whose direction agrees with the filtered queue trend before activation. A late render wake or capture discontinuity freezes estimation temporarily. The policy retains the 0.25-period deadband, 400-ppm-per-second slew limit, gradual polarity handling, and absolute +/-1,000 ppm bound, and returns toward nominal when evidence disappears. Requested/applied correction, estimated drift, activation state/duration, saturation, and direction changes remain separate diagnostics.

`IAudioClockAdjustment.SetSampleRate` executes only on the below-normal controller thread, never on an MMCSS packet thread. Shutdown restores nominal rate before ownership release. Deterministic tests cover equal clocks, sustained positive and negative drift, one late render wake, discontinuity freeze and recovery, bounded gradual correction, no polarity oscillation, and return to nominal. The ring remains bounded regardless of controller state.

## Non-real-time event delivery

Diagnostics use a capacity-one latest-value publisher and are delivered at most twice per second. State and failure notifications share a separate bounded eight-entry control publisher. Retained events keep their order, redundant identical pending state notifications are coalesced, and state entries yield queue capacity to a failure. Subscriber invocation is independent and exception-contained. A slow, blocked, or throwing observer never executes on or delays the MMCSS packet worker.

Both publishers drain or settle before session completion. If synchronous Stop times out, each worker's eventual `finally` disposes its events exactly once after use; no external second Stop is required. The same eventual path applies to the rate controller and its retained render client.

Diagnostics include safe endpoint names, formats, initialization path, native periods/buffers, current/average/maximum/capacity/target queue frames, capture/render/silence/loss counters, requested/applied correction, estimated drift and activation, device/QPC positions, volume/mute, and last packet/render times. Capture and render interval average/p95/max and long-gap counts use fixed bounded millisecond buckets. Capture packet sizes and render `framesAvailable` use fixed bounded distributions. Render padding, average/maximum available frames, multi-period late wakes, empty capture wakes, physical drops, consumer trims, ring starvation, native underfill, and capture/render frame rates are distinct. Publication remains throttled to twice per second and occurs on the supervisor/publisher path, not either packet thread.

Every capture discontinuity retains monotonic time, packet frames, position deltas, queue before/after, controller values, lifecycle phase, and whether a loss counter changed within the next 100 ms. A later unrelated loss is not retroactively attributed.

## Volume, video, fullscreen, and failures

Volume is bounded application gain with a short linear ramp. Mute also ramps rather than stepping. The themed slider preserves native keyboard and automation behavior and becomes unavailable with `No audio`.

Audio starts only after the first real video frame presents. Audio failure does not fault video, stop fullscreen, restart preview, reset video diagnostics, or hide the preview HWND. Fullscreen continues to reuse the same MainWindow, capture session, D3D11 renderer, swap chain, and child HWND.

Required customer wording is preserved:

- Startup: `Video is live. Audio monitoring could not start.`
- Runtime: `Video is live. Audio monitoring stopped.`
- Disconnected: `Video is live. The selected audio device disconnected.`

## Architecture-correction stability evidence

The available endpoints remained the safe-name Realtek microphone array and Realtek laptop speakers. Testing stayed muted at zero gain to prevent acoustic feedback. The production hybrid path negotiated shared 32-bit float, 48 kHz stereo with 480-frame capture/render periods and 1,056/2,238-frame native buffers. All baseline runs explicitly disabled rate adjustment.

Every muted run performed during this correction is retained:

- Initial two-period baseline, 600 seconds, before the exact preroll freeze was added: 203 ring starvations/native underfills, 576 physical-full rejected frames and 1,440 consumer-trimmed frames. Sampled queue min/average/max was 0/245.2/480 frames (internal maximum 2,880). Capture interval average/p95/max was 10.000/11/28.372 ms with packet distribution `96:1, 480:59801`; render was 10.000/11/40.353 ms with available distribution `480:59995, 1920:1`. Two startup discontinuities were recorded. This run exposed capture overshoot during render preparation and directly caused the preroll-barrier correction.
- Corrected two-period diagnostic, 120 seconds: 26 starvations/native underfills, 0 physical drops and 0 trims. Sampled queue min/average/max was 0/332.5/1,056. Capture interval average/p95/max was 10.009/11/119.825 ms with 26 empty wakes and packet distribution `96:2, 480:11971`; render was 10.004/11/57.598 ms with available distribution `480:11993, 2238:1`. Four startup/transition discontinuities were recorded.
- Corrected Stage A, two periods, 600 seconds: 26 starvations/native underfills, 0 physical drops and 192 consumer-trimmed frames; sampled queue min/average/max 0/720.1/1,056 (internal maximum 2,112). Capture interval average/p95/max was 10.002/11/120.050 ms with 24 empty wakes and distribution `96:3, 480:59942`; render was 10.001/11/55.783 ms with available distribution `480:59960, 960:1, 1920:1`. Four startup/transition discontinuities and one steady-state discontinuity near 101.6 seconds were retained. Stop completed and endpoints released.
- Corrected Stage B, three periods, 600 seconds: 6 starvations/native underfills, 0 physical drops and 0 trims; sampled queue min/average/max 0/726.7/1,056 (internal maximum 1,440). Capture interval average/p95/max was 10.001/11/100.181 ms with 6 empty wakes and distribution `96:1, 480:59992`; render was 10.001/11/81.298 ms with available distribution `480:59993, 2238:1`. One underfill occurred during startup/transition and five during steady state near 72–75 seconds. The three discontinuities were startup/transition observations. Stop completed and endpoints released.

Neither candidate passed the strict zero-loss gate, so no stable target is selected and no failed assertion was weakened. Both corrected ten-minute baselines showed near-equal 10.001–10.002 ms average event intervals but isolated 100–120 ms capture gaps/empty wakes. That is scheduling or device-delivery evidence, not sustained clock drift; Stage C was therefore not justified and evidence-driven adjustment remained disabled. The render worker did recover late wakes by requesting up to 1,920 or 2,238 frames in one release rather than one nominal period.

Twenty additional muted Start/Stop cycles passed in 22 seconds. Every cycle reacquired, stopped, and released the capture/render endpoints, and immediate endpoint enumeration still returned the Realtek input and output.

The laptop exposed only its open speakers; no wired or USB headphones/earphones were connected. Unmuted monitoring was intentionally not attempted because microphone-to-speaker feedback would violate the safety boundary. Audible sound, routing, crackling, gaps, pitch/warble, mute cycles, volume travel, fullscreen/minimize behavior, ten-minute listening, and close/reacquisition checks remain blocked and unclaimed. PR #6 therefore remains draft and Phase 5 is not complete.

## Mandatory remaining validation

Before merge and Microsoft Store release, Phase 5 still requires isolated wired/USB headphone validation, three passing ten-minute muted sessions (or one passing thirty-minute session), and physical USB HDMI capture-card audio testing. HDMI endpoint topology, format support, disconnect/no-signal behavior, A/V synchronization, and measured HDMI latency remain unknown. A laptop microphone does not establish HDMI compatibility.

Audio recording, muxing, Snapshot, reconnect, diagnostics export, system-volume control, and automatic audio/video endpoint association remain intentionally deferred.

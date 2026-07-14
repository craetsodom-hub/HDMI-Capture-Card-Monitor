# Phase 2A: low-latency GPU preview proof of concept

## Scope and status

Phase 2A proves a real, non-recording preview from the selected Phase 1 video device and native mode. It uses Media Foundation for acquisition and decode, Direct3D 11 for video processing, and a DXGI swap chain targeting the existing in-window child HWND. It does not establish HDMI compatibility or end-to-end capture latency.

Audio, recording, snapshots, fullscreen, always-on-top, image controls, hot-plug recovery, automatic reconnect, DirectShow fallback, third-party codecs, licensing, telemetry, and production settings remain intentionally excluded.

## Managed boundaries

- `MainWindowViewModel` owns commands, the existing lifecycle state machine, stale-operation generations, selections, and customer-facing messages.
- `ICapturePreviewService` coordinates at most one native session and exposes managed first-frame, diagnostic, and failure events.
- `MediaFoundationPreviewSession` owns the native worker thread, reader/source/media types, stop control, and deterministic cleanup.
- `D3D11PreviewRenderer` owns the D3D device/context, DXGI device manager, video processor, output resources, and HWND swap chain.
- `IPreviewSurface` exposes only a stable HWND, pixel size, availability/resize notifications, and presentation visibility controls.
- `PreviewDiagnosticsTracker` owns bounded managed counters and timing samples.

No Media Foundation or Direct3D COM object crosses into XAML, the view model, or public managed models.

## Capture thread and queue depth

Each active preview creates one explicitly owned background thread and calls `CoInitializeEx(COINIT_MULTITHREADED)` on it. That thread synchronously calls `IMFSourceReader.ReadSample`, renders the returned sample immediately, releases the sample/buffer/texture views, and only then requests the next sample.

The synchronous design was selected because Phase 2A needs one understandable ownership domain and no application queue. An `IMFSourceReaderCallback` and custom COM callback lifetime would add concurrency and ownership risk without proving a lower queue depth. Arbitrary thread-pool `Task.Run` loops are also avoided. Application frame-queue capacity is exactly zero; at most one sample is actively processed by application code.

## Native input and output negotiation

Preview reopens the selected device by its opaque symbolic link. It retrieves the selected native media-type index and verifies width, height, frame-rate numerator/denominator, subtype, and interlace information where present. `IMFSourceReaderEx.SetNativeMediaType` then applies that exact native type before the first sample request. A type-change flag caused by this explicit selection is expected; later unsolicited `CURRENTMEDIATYPECHANGED` or `NATIVEMEDIATYPECHANGED` reader flags are fatal typed failures.

Source Reader attributes request:

- `MF_LOW_LATENCY = TRUE`;
- the session `IMFDXGIDeviceManager` through `MF_SOURCE_READER_D3D_MANAGER`;
- `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = TRUE`;
- `MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN = TRUE`.

Output is requested as NV12 at the selected size and exact frame-rate rational. Native NV12 stays uncompressed. Native MJPEG is decoded by an installed Media Foundation decoder into GPU-backed NV12; there is no manual JPEG decode or software codec package. Negotiation fails honestly when the resulting media type is not NV12 or samples do not expose `IMFDXGIBuffer`.

## D3D11, DXGI, and GPU frame path

The renderer first attempts a hardware D3D11 device with BGRA and video-support flags at supported feature levels, then permits WARP only as an observable degraded fallback. D3D work remains on the preview thread, so multithread protection is not enabled. `MFCreateDXGIDeviceManager` creates the manager and `ResetDevice` associates the D3D device before Source Reader creation.

For every sample, the session obtains an `IMFMediaBuffer`, queries `IMFDXGIBuffer`, retrieves its `ID3D11Texture2D` and subresource index, creates a short-lived video-processor input view, and submits one `VideoProcessorBlt`. No normal-path texture map, managed byte array, `WriteableBitmap`, or retained old sample is used.

The HWND swap chain uses BGRA8, flip-discard, two buffers, and a frame-latency waitable-object flag. `IDXGISwapChain2.SetMaximumFrameLatency(1)` limits queued presentation. Each frame clears the render target black and presents immediately after the GPU video-processor blit.

## Fit rendering and resize

For source size `(sw, sh)` and output size `(tw, th)`, Fit uses `scale = min(tw/sw, th/sh)`, rounds the scaled dimensions to the nearest pixels while clamping them to the available bounds, and centers the destination rectangle. The black clear remains visible outside that rectangle, producing letterbox or pillarbox bars without stretching.

WPF converts device-independent dimensions using the current DPI scale. Resize notifications only replace the value in a capacity-one mailbox. `ResizeBuffers` and output-resource recreation happen on the preview thread, never on the UI thread. Empty output sizes skip presentation safely until a non-zero resize arrives.

## HwndHost airspace lifecycle

`HwndPreviewSurface` creates one child HWND without `WS_VISIBLE`, initially hidden and positioned off-screen, and destroys it in `DestroyWindowCore`. WPF `HwndHost` airspace is handled in two layers:

1. While inactive, the WPF host is hidden so the normal WPF placeholder remains visible.
2. Before D3D session ownership begins, the host is activated and then remains stable for the whole session; the native child remains hidden and off-screen until the first successful present.
3. First-frame notification shows the child and hides the WPF message.
4. Stop hides the child immediately, completes reader/source/D3D cleanup, and only then hides the WPF host to restore the placeholder.

This ordering was validated after an earlier implementation changed host visibility after presentation had begun and reproduced handled `DXGI_ERROR_DEVICE_REMOVED` failures during rapid repeated sessions. The corrected ordering completed the required paced cycle run; the earlier events remain recorded as evidence rather than being omitted.

## Stop, Flush, and native cleanup

`StopAsync` transitions to stopping exactly once, cancels the managed token, and starts a separate explicitly MTA control thread. Under the reader-publication lock, that control path calls `IMFSourceReader.Flush` for the first video stream only if the preview thread has not released the reader. The WPF thread never calls `Flush`.

The preview thread remains the only owner allowed to release native objects. Cleanup is a layered, non-throwing sequence: current/output/native media types; Source Reader; media-source shutdown and release; reader/source attributes; renderer output views/back buffer/video processor; DXGI manager, swap chain, factory/adapter, D3D context/device; COM apartment; cancellation source; and completion signal. `MF_E_SHUTDOWN` is accepted during source shutdown. Every other cleanup failure is logged through a failure-safe reporter and cannot prevent later releases. Completion is signalled in the outermost `finally`, so Stop cannot wait forever because one cleanup step threw. Per-frame sample, buffer, DXGI buffer, texture, and input view references are released in each loop iteration.

The stop caller waits no more than three seconds. A timeout does not release objects underneath the worker, prevents retry, and prevents application-level `MFShutdown` while preview workers remain unsettled. Repeated Stop and Dispose calls are idempotent. Window `Closing` disposes the view model synchronously before `HwndHost` destruction; `Closed` and application exit remain idempotent fallbacks.

Renderer construction uses a scoped ownership stack. Hardware-path resources are released in reverse acquisition order before WARP is attempted, and a failed WARP attempt receives the same cleanup. Null native outputs are rejected, output-resource construction failure disposes the completed renderer, and committed ownership is released once by idempotent renderer disposal.

## Flags, failures, and stall handling

Reader errors, media-type changes, end-of-stream, null samples, stream ticks, new streams, removed effects, failed reads, unavailable/busy/denied devices, decoder negotiation, unsupported GPU buffers, device removal/reset, presentation failure, and selected-mode mismatch map to managed safe categories. HRESULTs may appear in local technical logs but never customer-facing text; symbolic links never appear in either.

A managed watchdog uses `Stopwatch` timestamps and tracks sample arrival separately from successful presentation. Three seconds without a sample becomes a startup timeout before first frame or a video-input stall afterward. While the surface is minimized or zero-sized, continued samples keep the session healthy and presentation absence is ignored. Restore grants the normal presentation interval without restarting. A presentable surface that receives samples but cannot present becomes a typed presentation failure, not an input stall. The UI says "Video input stalled," not "HDMI no signal," because a physical HDMI source has not been tested.

After startup or runtime failure, failed-session cleanup completes before the service clears its active identity. If cleanup succeeds and the existing device/format selection is still valid, the lifecycle returns from `Faulted` to `DeviceReady` and Start is enabled again. Cleanup timeout deliberately leaves the UI faulted and blocks another native session. Events are accepted only for the currently bound session, so a queued failure from a retired or stopped-while-Starting session cannot fault a newer preview or overwrite Refresh state.

## Diagnostics and limitations

Each snapshot includes device display name, requested/actual native format, actual negotiated output width/height/frame-rate rational/subtype/interlace mode, Hardware/WARP driver, received/rendered frames and rolling FPS, sample-timestamp cadence, null samples, stream ticks, presentation failures, zero-size/minimize skips, `DXGI_ERROR_WAS_STILL_DRAWING` rejections, average/approximate-p95 application processing, average/approximate-p95 sample-return-to-present time, last sample timestamp, last successful frame time, consecutive read failures, and last safe failure category. Timing storage is bounded to 256 values and frame-rate storage to 120 timestamps; UI publication is throttled to twice per second.

The sample-return-to-present stopwatch starts immediately after `ReadSample` returns, before buffer extraction and interface queries. The narrower processing stopwatch starts immediately before renderer work. Neither includes the blocking wait for the next sample. These values are not camera-to-screen or HDMI latency measurements, and media timestamps do not establish end-to-end latency.

## HD Camera evidence

Test environment: local Windows x64 Release build, built-in `HD Camera`, Media Foundation startup `0x00000000`, hardware D3D11.

- Native NV12: selected `1280 x 720p - 30 fps - NV12`; actual negotiated output was NV12, 1280x720, 30/1, progressive, hardware D3D11. Live GPU preview, continuous width/height resize, maximize/restore, minimize/restore, and release after Stop were exercised.
- Native MJPEG: selected `1280 x 720p - 30 fps - MJPEG`; Media Foundation negotiated decoded GPU NV12 at 1280x720, 30/1, progressive. The focused run received/rendered 235/235 frames at 10.0/10.0 fps, sample-timestamp cadence 10.0 fps, 1.45 ms average and 2.40 ms approximate-p95 processing, 1.52/2.46 ms sample-return-to-present, zero presentation failures, and no final failure.
- Repeated lifecycle: 20 confirmed NV12 Start/Stop cycles passed. Every observed immediate Starting state retained the WPF placeholder; no stale-frame flash was seen. One additional scripted interaction was ambiguous and was not counted. A later stress retry genuinely stopped receiving samples; the watchdog reported `PreviewStalled`, cleanup returned the existing selection to `DeviceReady`, and retry succeeded without Refresh. One separate rapid-lifecycle stress event produced a handled `DXGI_ERROR_DEVICE_REMOVED`; cleanup and retry succeeded. These events are retained rather than omitted.
- Corrected continuous NV12 stability: the uninterrupted session received 7,609 frames at a final rolling/sample-timestamp cadence of 10.0 fps, proving about 760.9 seconds (12 minutes 40.9 seconds), including a measured 12.6-second minimize. It rendered 7,484 frames; the exact 125-frame difference equals the recorded minimized/zero-surface skips. Final diagnostics were 10.0 received / 10.0 rendered fps, 1.07/1.64 ms average/p95 processing, 1.11/1.71 ms average/p95 sample-return-to-present, zero `DXGI_ERROR_WAS_STILL_DRAWING`, zero presentation failures, one null sample, one stream tick, and no last failure.
- Cadence conclusion: although the selected native type and negotiated output type both say 30/1, the HD Camera delivered approximately 10 samples per second and its sample timestamps also measured 10.0 fps. Outside the intentional minimize skips, received and rendered totals/cadence matched. This evidence supports a camera-delivery cadence limitation under the test conditions, not application frame dropping, and does not support a 30-fps performance claim.
- Stop after the continuous run returned to `DeviceReady` in about 1.03 seconds. Windows Camera acquired the HD Camera with capture controls and no busy error after Stop and again after full application close. Closing during Starting completed in 798 ms; closing during Previewing completed in 649 ms; the process exited in both cases.
- The live evidence screenshot was captured from the real preview with a neutral dark/covered camera view and is intentionally stored outside the repository; no camera media is committed.

## Remaining requirements and risks

- A physical USB HDMI capture card and real HDMI source have not been tested. HDMI compatibility, no-signal behavior, bandwidth modes, disconnect semantics, and HDMI latency are not claimed.
- WARP is implemented only as a degraded fallback and was not the observed HD Camera backend.
- The three-second Flush bound is implemented; a truly unresponsive third-party driver could still leave a worker alive, in which case native ownership is intentionally retained until the thread settles.
- Device-removed/reset failures stop preview safely; automatic device recreation/reconnect is intentionally outside Phase 2A.
- The HD Camera image was visible with normal orientation. Fit rendering preserved its 16:9 geometry with black pillarboxing where the target surface was wider; this remains webcam evidence and is not used to make an HDMI claim.

Physical USB HDMI capture-card validation remains mandatory before Microsoft Store release.

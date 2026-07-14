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

`HwndPreviewSurface` creates one `WS_CHILD | WS_VISIBLE` child HWND and destroys it in `DestroyWindowCore`. WPF `HwndHost` airspace is handled in two layers:

1. While inactive, the WPF host is hidden so the normal WPF placeholder remains visible.
2. Before D3D session ownership begins, the host is activated and then remains stable for the whole session; the native child remains hidden and off-screen until the first successful present.
3. First-frame notification shows the child and hides the WPF message.
4. Stop hides the child immediately, completes reader/source/D3D cleanup, and only then hides the WPF host to restore the placeholder.

This ordering was validated after an earlier implementation changed host visibility after presentation had begun and reproduced handled `DXGI_ERROR_DEVICE_REMOVED` failures during rapid repeated sessions. The corrected ordering completed the required paced cycle run; the earlier events remain recorded as evidence rather than being omitted.

## Stop, Flush, and native cleanup

`StopAsync` transitions to stopping exactly once, cancels the managed token, and starts a separate explicitly MTA control thread. Under the reader-publication lock, that control path calls `IMFSourceReader.Flush` for the first video stream only if the preview thread has not released the reader. The WPF thread never calls `Flush`.

The preview thread remains the only owner allowed to release native objects. Cleanup is reverse-owned: current/output/native media types; Source Reader; media-source shutdown and release; reader/source attributes; renderer output views/back buffer/video processor; DXGI manager, swap chain, factory/adapter, D3D context/device; COM apartment. Per-frame sample, buffer, DXGI buffer, texture, and input view references are released in each loop iteration.

The stop caller waits no more than three seconds. A timeout does not release objects underneath the worker and prevents application-level `MFShutdown` while preview workers remain unsettled. Repeated Stop and Dispose calls are idempotent.

## Flags, failures, and stall handling

Reader errors, media-type changes, end-of-stream, null samples, stream ticks, new streams, removed effects, failed reads, unavailable/busy/denied devices, decoder negotiation, unsupported GPU buffers, device removal/reset, presentation failure, and selected-mode mismatch map to managed safe categories. HRESULTs may appear in local technical logs but never customer-facing text; symbolic links never appear in either.

A managed watchdog checks the last successful presentation twice per second. Three seconds without a presented frame records `PreviewStalled` and invokes the same cancellation plus bounded MTA Flush path. The UI says “Video input stalled,” not “HDMI no signal,” because a physical HDMI source has not been tested.

## Diagnostics and limitations

Each snapshot includes device display name, requested/actual native format, output subtype, Hardware/WARP driver, received/rendered frames, null samples, stream ticks, presentation failures, rendered FPS, average and approximate p95 application frame-processing time, last sample timestamp, last successful frame time, consecutive read failures, and last safe failure category. Timing storage is bounded to 256 values and frame-rate storage to 120 timestamps; UI publication is throttled to twice per second.

The processing stopwatch starts after GPU-buffer extraction and ends after presentation. It does not include the blocking wait for the next camera sample. These values are not camera-to-screen or HDMI latency measurements, and media timestamps do not establish end-to-end latency.

## HD Camera evidence

Test environment: local Windows x64 Release build, built-in `HD Camera`, Media Foundation startup `0x00000000`, hardware D3D11.

- Native NV12: `1280 Ã— 720p Â· 30 fps Â· NV12`; negotiated output NV12; live GPU preview, resize, maximize, minimize/restore, and release after Stop were exercised.
- Native MJPEG: `1280 Ã— 720p Â· 30 fps Â· MJPEG`; Media Foundation negotiated decoded GPU NV12; 255/255 frames rendered in the focused validation run, 10.0 rendered FPS under the camera conditions at that moment, 1.50 ms average processing, 2.20 ms approximate p95, and zero presentation failures.
- Repeated lifecycle: 20 paced NV12 Start/Stop cycles passed after the corrected HWND activation ordering. Two earlier aggressive pre-correction runs each produced one handled `DXGI_ERROR_DEVICE_REMOVED`; those failures drove the ordering correction and are retained as a known historical test observation.
- Corrected continuous NV12 stability: `613.5` seconds (10 minutes 13.5 seconds) remained in `Previewing` with a responsive UI after maximize, minimize/restore, and normal-window resize exercises. The final diagnostic snapshot recorded 9,666/9,666 frames received/rendered, hardware D3D11, NV12 output, 15.0 rolling rendered FPS, 1.27 ms average application processing, 1.85 ms approximate p95 processing, one null sample, one stream tick, zero presentation failures, and no last failure. The measured aggregate rate was approximately 15.7 rendered FPS; sampled UI FPS ranged from 15.0 to 30.1 and settled at 15.0. These are application throughput measurements, not end-to-end latency.
- Stop after the continuous run returned to `Device ready` in 1.09 seconds. Windows Camera then acquired and displayed the HD Camera, confirming release. Closing HDMI Capture Card Monitor during a later active preview completed in 0.42 seconds; Windows Camera acquired and displayed the device again afterward. No camera-busy message, D3D device-removed/reset event, read failure, render failure, or cleanup timeout occurred in the corrected run.
- Automated race tests cover Stop/disposal during Starting, double Start, repeated Stop, stale events, and disposal during Previewing. The manual active-preview close and post-close release checks above exercised the native shutdown path with real hardware.

## Remaining requirements and risks

- A physical USB HDMI capture card and real HDMI source have not been tested. HDMI compatibility, no-signal behavior, bandwidth modes, disconnect semantics, and HDMI latency are not claimed.
- WARP is implemented only as a degraded fallback and was not the observed HD Camera backend.
- The three-second Flush bound is implemented; a truly unresponsive third-party driver could still leave a worker alive, in which case native ownership is intentionally retained until the thread settles.
- Device-removed/reset failures stop preview safely; automatic device recreation/reconnect is intentionally outside Phase 2A.
- The HD Camera image was visible with normal orientation. Fit rendering preserved its 16:9 geometry with black pillarboxing where the target surface was wider; this remains webcam evidence and is not used to make an HDMI claim.

Physical USB HDMI capture-card validation remains mandatory before Microsoft Store release.

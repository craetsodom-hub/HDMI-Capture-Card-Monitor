# Architecture

## Foundation

The application is .NET 8 WPF, x64-only, using MVVM through `CommunityToolkit.Mvvm`. Media Foundation is the intended primary capture backend; DirectShow is a narrowly scoped compatibility fallback. No native wrapper has been selected in Phase 0: candidate wrappers and raw interop must be documented and validated against supported formats, lifetime behavior, Store packaging, and latency before adoption.

## Boundaries

- `ICaptureDeviceEnumerator`: enumerate video/audio devices and capabilities.
- `ICaptureBackend`: negotiate and own an active device stream.
- `IVideoPreviewRenderer`: present negotiated video samples.
- `IAudioMonitor`: route audio samples to local playback.
- `IRecordingService`: own optional recording pipeline and output lifecycle.
- `IDeviceRecoveryService`: coordinate device-loss detection and recovery.
- `ICaptureDiagnostics`: produce safe technical observations and support data.

These are contracts only in Phase 0; no fake or production implementations exist. Device discovery, preview, rendering, audio, recording, diagnostics, and recovery remain separate responsibilities. View models orchestrate state and user intent, but do not own native media handles.

## Threading, lifetime, and performance

Every asynchronous operation must take a cancellation token where appropriate. UI updates marshal to the WPF dispatcher; device and media work stays off the UI thread. Native COM, streams, device handles, and cancellation registrations have one clear owner and are deterministically disposed in the reverse order of acquisition. Shutdown is idempotent.

The final preview path will use bounded backpressure with no unbounded video or audio queues. It should avoid unnecessary frame copies; OpenCV is explicitly not part of the normal preview path. Renderer/back-end transfer semantics will be chosen to avoid buffer ownership ambiguity.

## Errors, logs, settings, and tests

Backends return typed, contextual failures upward; the UI translates them into plain-language status while diagnostics retains safe technical detail. There are no silent fallbacks: choosing DirectShow must be observable in logs and diagnostics. Local logging supports Debug, Information, Warning, and Error. It stores text only and excludes media and sensitive user data.

Settings will be local, versioned, validated, atomic on write, and Store-compatible. Hardware and native integration stay behind interfaces; state, capability decisions, validation, settings serialization, and error mapping are unit-testable without hardware. Hardware tests remain manual/integration tests in the QA matrix.

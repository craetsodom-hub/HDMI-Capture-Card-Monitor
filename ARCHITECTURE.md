# Architecture

## Foundation

The application is stable .NET 10 WPF, x64-only, using MVVM through `CommunityToolkit.Mvvm`. Media Foundation is the intended primary capture backend; DirectShow is a narrowly scoped compatibility fallback. No native wrapper has been selected in Phase 0: candidate wrappers and raw interop must be documented and validated against supported formats, lifetime behavior, Store packaging, and latency before adoption.

## Boundaries

- Device enumeration: enumerate video/audio devices and capabilities.
- Capture backend: negotiate and own an active device stream.
- Video preview renderer: present negotiated video samples.
- Audio monitor: route audio samples to local playback.
- Recording service: own optional recording pipeline and output lifecycle.
- Device recovery service: coordinate device-loss detection and recovery.
- Capture diagnostics: produce safe technical observations and support data.

These are conceptual boundaries in Phase 0.1; no speculative code-level interfaces, fake implementations, or production implementations exist. Real contracts will be introduced only when their phase defines inputs, outputs, ownership, cancellation, errors, and disposal. Device discovery, preview, rendering, audio, recording, diagnostics, and recovery remain separate responsibilities. View models orchestrate state and user intent, but do not own native media handles.

## Threading, lifetime, and performance

Every asynchronous operation must take a cancellation token where appropriate. UI updates marshal to the WPF dispatcher; device and media work stays off the UI thread. Native COM, streams, device handles, and cancellation registrations have one clear owner and are deterministically disposed in the reverse order of acquisition. Shutdown is idempotent.

The final preview path will use bounded backpressure with no unbounded video or audio queues. It should avoid unnecessary frame copies; OpenCV is explicitly not part of the normal preview path. Renderer/back-end transfer semantics will be chosen to avoid buffer ownership ambiguity.

## Errors, logs, settings, and tests

Backends return typed, contextual failures upward; the UI translates them into plain-language status while diagnostics retains safe technical detail. There are no silent fallbacks: choosing DirectShow must be observable in logs and diagnostics. Local logging supports Debug, Information, Warning, and Error. It stores text only and excludes media and sensitive user data. Logger creation is centralized: if local files cannot be created, a local no-op logger is used and the shell displays a non-fatal notice. File names are collision-resistant and retention is bounded to matching application logs.

The state model permits `Previewing → Recording → Previewing`; stopping recording must not imply stopping the live preview.

## Phase 1 discovery

Phase 1 uses generated CsWin32 Media Foundation bindings behind `ICaptureDeviceDiscoveryService`. Native work runs synchronously on background COM-initialized workers and returns immutable managed device and capability models. Symbolic links are opaque identities and never appear in UI or logs. The service does not read frames or render video.

Settings will be local, versioned, validated, atomic on write, and Store-compatible. Hardware and native integration stay behind interfaces; state, capability decisions, validation, settings serialization, and error mapping are unit-testable without hardware. Hardware tests remain manual/integration tests in the QA matrix.

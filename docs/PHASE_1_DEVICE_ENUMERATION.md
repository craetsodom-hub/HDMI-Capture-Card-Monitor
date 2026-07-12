# Phase 1: Device Enumeration

Phase 1 uses Media Foundation only because it is the Windows-native capture stack and exposes video-capture device activation and native media types without starting a preview. `Microsoft.Windows.CsWin32` 0.3.298 (MIT) generates the required Windows bindings at build time; it is a private source-generator dependency and is not deployed as a runtime library.

`NativeMethods.txt` requests only COM initialization, memory freeing, Media Foundation startup/shutdown, device-source enumeration/activation, source-reader creation, Media Foundation attributes, and the device/media-type constants used by this phase. Generated output is not committed.

Each discovery operation runs synchronously inside a background worker after explicit COM initialization. `MFStartup` is lazily owned by `MediaFoundationRuntime` and balanced by one `MFShutdown` during application shutdown. Successful COM initialization, including `S_FALSE`, is balanced with `CoUninitialize`; `RPC_E_CHANGED_MODE` is reported as a discovery failure.

Device identity is the Media Foundation video symbolic link, held as an opaque ID and never shown or logged. Friendly names are non-unique and are normalized with stable suffixes. Webcams are valid Windows video inputs; the application never guesses that a device is HDMI from its name.

For a selected device only, the service recreates activation attributes from the opaque ID, opens a media source and source reader, enumerates native video media types, converts them into immutable managed capabilities, then releases the reader before shutting down/releasing the source and attributes. It never reads a frame, configures RGB output, or leaves a device open. Cancellation and request generations prevent stale UI updates.

Discovery errors retain an operation and safe HRESULT for diagnostics. The UI uses generic actionable text and never exposes symbolic links or COM stack traces. Current local evidence: Media Foundation initialization returned `0x80004001`, so no-device/camera enumeration could not be performed on this machine. No HDMI adapter, USB capture card, or webcam validation is claimed. Preview, audio, snapshots, and recording remain unimplemented.
# Phase 1 device discovery notes

Media Foundation is initialized once by `App.OnStartup`, after logging is available and before a discovery service is composed. The application uses the generated `PInvoke.MF_VERSION` directly. A failed initialization composes an unavailable discovery service instead of retrying startup for every Refresh.

Discovery workers are cancellation-aware and tracked by the service. Shutdown cancels new work and waits up to three seconds. If a worker is still active, Media Foundation shutdown is deliberately skipped and a warning is logged; this avoids shutting it down underneath native work during process termination.

CsWin32 exposes the device activation array as unmanaged pointers, which are released with `Release` and `CoTaskMemFree`. The remaining generated Media Foundation interfaces are RCWs and are released in reverse acquisition order with `Marshal.ReleaseComObject`; media sources are shut down before release. This boundary is explicit because it follows the generated signatures rather than converting ownership models implicitly.

Phase 1 scans only the source reader's first-video-stream selector. Capabilities retain their native media-type index for diagnostic/stable selection only; it is not part of equivalence. Mandatory malformed metadata is not accepted as a valid format. No preview, audio, recording, or snapshot features are included.

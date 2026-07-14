# Phase 1: Video Device and Native Format Discovery

Phase 1 uses Windows Media Foundation to enumerate video-capture devices and inspect the native video formats of the selected device. It does not read frames or implement preview, audio, recording, snapshots, or fullscreen.

## Runtime lifetime

`App.OnStartup` creates the logger and then calls `MFStartup(PInvoke.MF_VERSION)` exactly once before composing discovery services. `MediaFoundationRuntime` caches the exact first startup result, including its HRESULT and typed classification, and returns that same result on later initialization calls. A successful startup is balanced by one checked `MFShutdown`; shutdown failures are logged safely.

Startup failures are classified as missing media components (`E_NOTIMPL`), unsupported startup version (`MF_E_BAD_STARTUP_VERSION`), disabled in Safe Mode (`MF_E_DISABLED_IN_SAFEMODE`), or other failure. CsWin32 does not emit the Media Foundation error constants requested from the installed metadata, so the otherwise generated interop surface uses one centralized set of values sourced from the installed Windows SDK `Mferror.h` rather than scattered numeric literals.

## Discovery and shutdown

Every background discovery operation is registered under a lifecycle lock before it can run. Shutdown stops accepting operations, cancels the shared shutdown token, snapshots active tasks under the lock, and waits outside the lock for at most three seconds. Completed tasks remove themselves through a guaranteed continuation. If a native worker exceeds the bound, its cancellation resources remain alive and `MFShutdown` is skipped rather than being called underneath active Media Foundation work.

Workers balance every successful COM initialization with `CoUninitialize`. Device activations and the activation array are released exactly once. Friendly names and the hardware-source flag are optional; missing friendly names become `Unnamed video device`, and unavailable hardware-source metadata remains `null`. The symbolic link is a mandatory opaque identifier and is never displayed or logged.

For format discovery, the source reader enumerates only the first-video-stream selector. `MF_E_NO_MORE_TYPES` is the sole normal end condition. Each type must be video and contain a subtype, non-zero dimensions, and a frame-rate rational with a non-zero denominator. A malformed type is logged without device identifiers and skipped so later valid types remain available.

Interlace values map as follows: `2` is progressive; `3`, `4`, `5`, and `6` are interlaced; `7` is mixed; all other values are unknown. Pixel aspect ratio and interlace metadata are optional. Exact frame-rate numerators and denominators remain authoritative.

Native cleanup is ordered: release the source reader, call media-source `Shutdown`, release the media source, release attributes, then uninitialize COM. `MF_E_SHUTDOWN` means the source was already shut down and is accepted without warning. Other shutdown HRESULTs are logged while remaining releases continue.

## Validation

The ordinary hardware-independent suite is run with `--filter "Category!=Hardware"`. The physical integration test is explicitly opt-in using `HDMI_CAPTURE_HARDWARE_VALIDATION=1` and `--filter "Category=Hardware"`; it fails with clear instructions if accidentally run without opt-in.

Local hardware validation succeeded with the built-in **HD Camera**: Media Foundation startup succeeded, the device was found, 15 native formats were discovered, repeated capability discovery succeeded, repeated refresh succeeded, and disposal during discovery completed safely. Validation with a physical USB HDMI capture card remains outstanding.

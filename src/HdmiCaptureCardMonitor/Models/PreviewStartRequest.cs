using HdmiCaptureCardMonitor.Capture.Abstractions;

namespace HdmiCaptureCardMonitor.Models;

public sealed record PreviewStartRequest(
    CaptureDevice Device,
    NativeVideoCapability NativeFormat,
    IPreviewSurface Surface,
    CancellationToken CancellationToken);

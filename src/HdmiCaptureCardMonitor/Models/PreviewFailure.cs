namespace HdmiCaptureCardMonitor.Models;

public sealed record PreviewFailure(
    PreviewFailureCategory Category,
    string SafeMessage,
    int? HResult = null,
    Exception? Exception = null);

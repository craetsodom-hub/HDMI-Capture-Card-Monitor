namespace HdmiCaptureCardMonitor.Models;

public sealed record PreviewStartResult(bool IsSuccess, bool IsCancelled, Guid SessionId, PreviewFailure? Failure)
{
    public static PreviewStartResult Started(Guid sessionId) => new(true, false, sessionId, null);
    public static PreviewStartResult Cancelled(Guid sessionId) => new(false, true, sessionId, null);
    public static PreviewStartResult Failed(Guid sessionId, PreviewFailure failure) => new(false, false, sessionId, failure);
}

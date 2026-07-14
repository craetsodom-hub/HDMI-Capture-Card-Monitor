namespace HdmiCaptureCardMonitor.Models;

public sealed record PreviewStopResult(bool IsSuccess, bool TimedOut, PreviewFailure? Failure)
{
    public static PreviewStopResult Stopped { get; } = new(true, false, null);
    public static PreviewStopResult Timeout(PreviewFailure failure) => new(false, true, failure);
    public static PreviewStopResult Failed(PreviewFailure failure) => new(false, false, failure);
}

namespace HdmiCaptureCardMonitor.Models;

public enum MediaFoundationStartupStatus
{
    Success,
    MissingMediaComponents,
    UnsupportedStartupVersion,
    DisabledInSafeMode,
    OtherFailure
}

public sealed record MediaFoundationStartupResult(MediaFoundationStartupStatus Status, int? HResult)
{
    public bool IsSuccess => Status == MediaFoundationStartupStatus.Success;
}

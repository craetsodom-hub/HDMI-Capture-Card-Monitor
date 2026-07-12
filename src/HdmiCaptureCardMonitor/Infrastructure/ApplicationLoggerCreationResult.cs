namespace HdmiCaptureCardMonitor.Infrastructure;

public sealed record ApplicationLoggerCreationResult(IApplicationLogger Logger, string? StartupNotice)
{
    public bool IsFileLoggingAvailable => StartupNotice is null;
}

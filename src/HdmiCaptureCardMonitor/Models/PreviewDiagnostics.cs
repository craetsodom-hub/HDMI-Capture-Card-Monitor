namespace HdmiCaptureCardMonitor.Models;

public sealed record PreviewDiagnostics(
    string DeviceDisplayName,
    string RequestedNativeFormat,
    string ActualNativeFormat,
    string NegotiatedOutputSubtype,
    PreviewDriverType DriverType,
    long FramesReceived,
    long FramesRendered,
    long NullSamples,
    long StreamTicks,
    long PresentationFailures,
    double RenderedFramesPerSecond,
    double AverageProcessingMilliseconds,
    double P95ProcessingMilliseconds,
    long? LastSampleTimestamp,
    DateTimeOffset? LastSuccessfulFrameTime,
    int ConsecutiveReadFailures,
    PreviewFailureCategory? LastFailureCategory)
{
    public static PreviewDiagnostics Empty(string device, string requestedFormat) => new(
        device,
        requestedFormat,
        string.Empty,
        string.Empty,
        PreviewDriverType.Hardware,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        0,
        null);
}

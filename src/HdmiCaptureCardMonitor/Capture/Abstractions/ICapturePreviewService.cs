using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Abstractions;

public interface ICapturePreviewService : IDisposable
{
    bool IsActive { get; }
    event EventHandler? IsActiveChanged;
    event EventHandler<PreviewSessionEventArgs>? FirstFramePresented;
    event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated;
    event EventHandler<PreviewFailureEventArgs>? PreviewFailed;
    Task<PreviewStartResult> StartAsync(PreviewStartRequest request);
    Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default);
}

public class PreviewSessionEventArgs(Guid sessionId) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
}

public sealed class PreviewDiagnosticsEventArgs(Guid sessionId, PreviewDiagnostics diagnostics) : PreviewSessionEventArgs(sessionId)
{
    public PreviewDiagnostics Diagnostics { get; } = diagnostics;
}

public sealed class PreviewFailureEventArgs(Guid sessionId, PreviewFailure failure) : PreviewSessionEventArgs(sessionId)
{
    public PreviewFailure Failure { get; } = failure;
}

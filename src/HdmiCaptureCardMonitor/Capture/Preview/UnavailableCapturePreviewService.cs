using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Preview;

internal sealed class UnavailableCapturePreviewService(PreviewFailure failure) : ICapturePreviewService
{
    public bool IsActive => false;
    public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented { add { } remove { } }
    public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated { add { } remove { } }
    public event EventHandler<PreviewFailureEventArgs>? PreviewFailed { add { } remove { } }
    public Task<PreviewStartResult> StartAsync(PreviewStartRequest request) => Task.FromResult(PreviewStartResult.Failed(Guid.Empty, failure));
    public Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(PreviewStopResult.Stopped);
    public void Dispose() { }
}

using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Preview;

internal sealed class MediaFoundationCapturePreviewService(IApplicationLogger logger) : ICapturePreviewService
{
    private readonly object syncRoot = new();
    private MediaFoundationPreviewSession? activeSession;
    private bool disposed;

    public bool IsActive { get { lock (syncRoot) return activeSession is not null; } }
    public bool WorkersSettled { get { lock (syncRoot) return activeSession is null || activeSession.IsCompleted; } }

    public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented;
    public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated;
    public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;

    public async Task<PreviewStartResult> StartAsync(PreviewStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaFoundationPreviewSession session;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeSession is not null)
            {
                return PreviewStartResult.Failed(
                    activeSession.SessionId,
                    new PreviewFailure(PreviewFailureCategory.DeviceBusy, "A preview session is already active."));
            }

            session = new MediaFoundationPreviewSession(request, logger);
            Subscribe(session);
            activeSession = session;
        }

        var result = await session.StartAsync().ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var stopResult = await session.StopAsync().ConfigureAwait(false);
            if (!stopResult.TimedOut) ClearIfCurrent(session);
            else _ = ClearWhenCompletedAsync(session);
        }
        return result;
    }

    public async Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        MediaFoundationPreviewSession? session;
        lock (syncRoot) session = activeSession;
        if (session is null) return PreviewStopResult.Stopped;

        var result = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut) ClearIfCurrent(session);
        else _ = ClearWhenCompletedAsync(session);
        return result;
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
        }

        var result = StopAsync().GetAwaiter().GetResult();
        if (result.TimedOut) logger.Warning("The preview service retained its native session because bounded shutdown timed out.");
    }

    private void Subscribe(MediaFoundationPreviewSession session)
    {
        session.FirstFramePresented += OnFirstFramePresented;
        session.DiagnosticsUpdated += OnDiagnosticsUpdated;
        session.PreviewFailed += OnPreviewFailed;
    }

    private void Unsubscribe(MediaFoundationPreviewSession session)
    {
        session.FirstFramePresented -= OnFirstFramePresented;
        session.DiagnosticsUpdated -= OnDiagnosticsUpdated;
        session.PreviewFailed -= OnPreviewFailed;
    }

    private void ClearIfCurrent(MediaFoundationPreviewSession session)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeSession, session)) return;
            Unsubscribe(session);
            activeSession = null;
        }
    }

    private async Task ClearWhenCompletedAsync(MediaFoundationPreviewSession session)
    {
        await session.Completion.ConfigureAwait(false);
        ClearIfCurrent(session);
    }

    private void OnFirstFramePresented(object? sender, PreviewSessionEventArgs e) => FirstFramePresented?.Invoke(this, e);
    private void OnDiagnosticsUpdated(object? sender, PreviewDiagnosticsEventArgs e) => DiagnosticsUpdated?.Invoke(this, e);
    private void OnPreviewFailed(object? sender, PreviewFailureEventArgs e) => PreviewFailed?.Invoke(this, e);
}

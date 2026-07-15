using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal sealed class WasapiAudioMonitorService : IAudioMonitorService
{
    private readonly IApplicationLogger logger;
    private readonly Func<AudioMonitorStartRequest, IAudioMonitorSession> sessionFactory;
    private readonly object syncRoot = new();
    private IAudioMonitorSession? activeSession;
    private bool disposed;

    public WasapiAudioMonitorService(IApplicationLogger logger) : this(logger, true) { }

    internal WasapiAudioMonitorService(IApplicationLogger logger, bool preferAudioClient3)
    {
        this.logger = logger;
        sessionFactory = request => new WasapiAudioMonitorSession(request, logger, preferAudioClient3);
    }

    internal WasapiAudioMonitorService(
        IApplicationLogger logger,
        Func<AudioMonitorStartRequest, IAudioMonitorSession> sessionFactory)
    {
        this.logger = logger;
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public bool IsActive { get { lock (syncRoot) return activeSession is not null; } }
    public Guid? ActiveSessionId { get { lock (syncRoot) return activeSession?.SessionId; } }
    public bool WorkersSettled { get { lock (syncRoot) return activeSession is null || activeSession.Completion.IsCompleted; } }

    public event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged;
    public event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated;
    public event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed;

    public async Task<AudioMonitorStartResult> StartAsync(AudioMonitorStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IAudioMonitorSession session;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeSession is not null)
                return AudioMonitorStartResult.Failed(activeSession.SessionId,
                    new AudioMonitorFailure(AudioMonitorFailureCategory.DeviceInUse, "Audio monitoring is already active."));
            session = sessionFactory(request);
            Subscribe(session);
            activeSession = session;
        }

        var result = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var stopped = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!stopped.TimedOut) ClearIfCurrent(session);
            else _ = ClearWhenCompletedAsync(session);
        }
        return result;
    }

    public async Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        IAudioMonitorSession? session;
        lock (syncRoot) session = activeSession;
        if (session is null) return AudioMonitorStopResult.Stopped;
        var result = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut) ClearIfCurrent(session);
        else _ = ClearWhenCompletedAsync(session);
        return result;
    }

    public void SetVolume(double volumePercent)
    {
        lock (syncRoot) activeSession?.SetVolume(volumePercent);
    }

    public void SetMuted(bool muted)
    {
        lock (syncRoot) activeSession?.SetMuted(muted);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
        }
        var result = StopAsync().GetAwaiter().GetResult();
        if (result.TimedOut) SafeWarning("The audio monitor retained its worker because bounded shutdown timed out.");
    }

    private void Subscribe(IAudioMonitorSession session)
    {
        session.StateChanged += ForwardState;
        session.DiagnosticsUpdated += ForwardDiagnostics;
        session.MonitoringFailed += ForwardFailure;
    }

    private void Unsubscribe(IAudioMonitorSession session)
    {
        session.StateChanged -= ForwardState;
        session.DiagnosticsUpdated -= ForwardDiagnostics;
        session.MonitoringFailed -= ForwardFailure;
    }

    private void ClearIfCurrent(IAudioMonitorSession session)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeSession, session)) return;
            Unsubscribe(session);
            activeSession = null;
        }
    }

    private async Task ClearWhenCompletedAsync(IAudioMonitorSession session)
    {
        await session.Completion.ConfigureAwait(false);
        ClearIfCurrent(session);
    }

    private void ForwardState(object? sender, AudioMonitorStateChangedEventArgs e) =>
        SafeAudioEventDispatch.Publish(StateChanged, this, e, logger, "service state");

    private void ForwardDiagnostics(object? sender, AudioMonitorDiagnosticsEventArgs e) =>
        SafeAudioEventDispatch.Publish(DiagnosticsUpdated, this, e, logger, "service diagnostics");

    private void ForwardFailure(object? sender, AudioMonitorFailureEventArgs e) =>
        SafeAudioEventDispatch.Publish(MonitoringFailed, this, e, logger, "service failure");

    private void SafeWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }
}

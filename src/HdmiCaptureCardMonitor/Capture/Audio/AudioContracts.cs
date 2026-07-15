namespace HdmiCaptureCardMonitor.Capture.Audio;

public interface IAudioEndpointDiscoveryService : IDisposable
{
    Task<AudioEndpointDiscoveryResult> EnumerateActiveEndpointsAsync(CancellationToken cancellationToken = default);
}

public interface IAudioMonitorService : IDisposable
{
    bool IsActive { get; }
    Guid? ActiveSessionId { get; }
    event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged;
    event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated;
    event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed;
    Task<AudioMonitorStartResult> StartAsync(AudioMonitorStartRequest request, CancellationToken cancellationToken = default);
    Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default);
    void SetVolume(double volumePercent);
    void SetMuted(bool muted);
}

public interface IAudioMonitorSession : IAsyncDisposable
{
    Guid SessionId { get; }
    AudioMonitorState State { get; }
    Task Completion { get; }
    event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged;
    event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated;
    event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed;
    Task<AudioMonitorStartResult> StartAsync(CancellationToken cancellationToken = default);
    Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default);
    void SetVolume(double volumePercent);
    void SetMuted(bool muted);
}

public sealed class AudioMonitorStateChangedEventArgs(
    Guid sessionId,
    AudioMonitorState previousState,
    AudioMonitorState currentState) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public AudioMonitorState PreviousState { get; } = previousState;
    public AudioMonitorState CurrentState { get; } = currentState;
}

public sealed class AudioMonitorDiagnosticsEventArgs(
    Guid sessionId,
    AudioMonitorDiagnostics diagnostics) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public AudioMonitorDiagnostics Diagnostics { get; } = diagnostics;
}

public sealed class AudioMonitorFailureEventArgs(
    Guid sessionId,
    AudioMonitorFailure failure) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public AudioMonitorFailure Failure { get; } = failure;
}

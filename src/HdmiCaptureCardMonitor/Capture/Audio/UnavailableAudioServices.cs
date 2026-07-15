namespace HdmiCaptureCardMonitor.Capture.Audio;

internal sealed class UnavailableAudioEndpointDiscoveryService : IAudioEndpointDiscoveryService
{
    public Task<AudioEndpointDiscoveryResult> EnumerateActiveEndpointsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AudioEndpointDiscoveryResult.Succeeded([], [], null));

    public void Dispose() { }
}

internal sealed class UnavailableAudioMonitorService : IAudioMonitorService
{
    public bool IsActive => false;
    public Guid? ActiveSessionId => null;
    public event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged { add { } remove { } }
    public event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated { add { } remove { } }
    public event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed { add { } remove { } }

    public Task<AudioMonitorStartResult> StartAsync(AudioMonitorStartRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(AudioMonitorStartResult.Failed(Guid.Empty,
            new AudioMonitorFailure(AudioMonitorFailureCategory.AudioServiceNotRunning, "Audio monitoring is unavailable.")));

    public Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AudioMonitorStopResult.Stopped);

    public void SetVolume(double volumePercent) { }
    public void SetMuted(bool muted) { }
    public void Dispose() { }
}

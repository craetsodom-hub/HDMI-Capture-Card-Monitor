namespace HdmiCaptureCardMonitor.Capture.Audio;

public enum AudioEndpointDataFlow
{
    Capture,
    Render
}

public enum AudioMonitorState
{
    Off,
    WaitingForVideo,
    Starting,
    Monitoring,
    Muted,
    Stopping,
    Faulted
}

public enum AudioMonitorFailureCategory
{
    AccessDenied,
    CaptureEndpointUnavailable,
    RenderEndpointUnavailable,
    DeviceInUse,
    UnsupportedAudioFormat,
    AudioServiceNotRunning,
    EndpointCreateFailed,
    DeviceInvalidated,
    ResourcesInvalidated,
    BufferFailure,
    StartupTimeout,
    StopTimeout,
    AudioProcessingFailure,
    OtherFailure
}

public enum AudioMonitorInitializationPath
{
    AudioClient3,
    AudioClient3CaptureClassicRender,
    ClassicSharedFallback
}

public enum AudioDiscontinuityPhase
{
    Startup,
    Transition,
    SteadyState
}

public sealed record AudioDiscontinuityObservation(
    TimeSpan MonotonicTime,
    int PacketFrameCount,
    long? DevicePositionDelta,
    long? QpcPositionDelta,
    int QueueBeforeFrames,
    int QueueAfterFrames,
    double? RequestedRateAdjustmentPpm,
    double? AppliedRateAdjustmentPpm,
    long UnderrunCountAtObservation,
    long OverrunCountAtObservation,
    AudioDiscontinuityPhase Phase,
    bool UnderrunFollowed = false,
    bool OverrunFollowed = false);

/// <summary>
/// Managed endpoint metadata. Id is deliberately opaque and ToString returns only
/// the customer-safe display name so normal binding and interpolation cannot leak it.
/// </summary>
public sealed class AudioEndpoint
{
    public AudioEndpoint(
        string id,
        string? displayName,
        AudioEndpointDataFlow dataFlow,
        string? deviceDescription = null,
        string? interfaceFriendlyName = null,
        Guid? containerId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An opaque endpoint identifier is required.", nameof(id));
        Id = id;
        DataFlow = dataFlow;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? dataFlow == AudioEndpointDataFlow.Capture ? "Unnamed audio input" : "Unnamed audio output"
            : displayName.Trim();
        DeviceDescription = NormalizeOptional(deviceDescription);
        InterfaceFriendlyName = NormalizeOptional(interfaceFriendlyName);
        ContainerId = containerId;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public AudioEndpointDataFlow DataFlow { get; }
    public string? DeviceDescription { get; }
    public string? InterfaceFriendlyName { get; }
    public Guid? ContainerId { get; }

    public override string ToString() => DisplayName;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AudioStreamFormat
{
    private const ushort FloatBitsPerSample = 32;
    private const int BytesPerFloatSample = sizeof(float);

    private AudioStreamFormat(int sampleRate, ushort channelCount, uint channelMask)
    {
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        ChannelMask = channelMask;
        BitsPerSample = FloatBitsPerSample;
        BlockAlignment = checked((ushort)(channelCount * BytesPerFloatSample));
        AverageBytesPerSecond = checked(sampleRate * BlockAlignment);
    }

    public int SampleRate { get; }
    public ushort ChannelCount { get; }
    public uint ChannelMask { get; }
    public ushort BitsPerSample { get; }
    public ushort BlockAlignment { get; }
    public int AverageBytesPerSecond { get; }
    public static string SampleEncoding => "32-bit IEEE float";

    public static AudioStreamFormat CreateIeeeFloat(int sampleRate, int channelCount, uint channelMask = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelCount, ushort.MaxValue);
        return new AudioStreamFormat(sampleRate, checked((ushort)channelCount), channelMask);
    }

    public int FramesToSampleCount(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        return checked(frameCount * ChannelCount);
    }

    public int FramesToByteCount(int frameCount) =>
        checked(FramesToSampleCount(frameCount) * BytesPerFloatSample);

    public double FramesToMilliseconds(long frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        return frameCount * 1000d / SampleRate;
    }

    public override string ToString() =>
        $"{SampleEncoding}, {SampleRate} Hz, {ChannelCount} channel{(ChannelCount == 1 ? string.Empty : "s")}";
}

public sealed record AudioMonitorFailure(
    AudioMonitorFailureCategory Category,
    string CustomerMessage,
    int? HResult = null,
    Exception? Exception = null)
{
    public override string ToString() => CustomerMessage;
}

public sealed class AudioMonitorStartRequest
{
    public AudioMonitorStartRequest(
        AudioEndpoint captureEndpoint,
        AudioEndpoint? renderEndpoint,
        double initialVolumePercent = 100,
        bool initiallyMuted = false)
    {
        CaptureEndpoint = captureEndpoint ?? throw new ArgumentNullException(nameof(captureEndpoint));
        if (captureEndpoint.DataFlow != AudioEndpointDataFlow.Capture)
            throw new ArgumentException("The selected input must be a capture endpoint.", nameof(captureEndpoint));
        if (renderEndpoint?.DataFlow == AudioEndpointDataFlow.Capture)
            throw new ArgumentException("The selected output must be a render endpoint.", nameof(renderEndpoint));
        InitialVolumePercent = AudioGainController.ClampVolumePercent(initialVolumePercent);
        InitiallyMuted = initiallyMuted;
        RenderEndpoint = renderEndpoint;
    }

    public AudioEndpoint CaptureEndpoint { get; }
    /// <summary>Null selects the current system-default multimedia output.</summary>
    public AudioEndpoint? RenderEndpoint { get; }
    public bool UsesSystemDefaultOutput => RenderEndpoint is null;
    public double InitialVolumePercent { get; }
    public bool InitiallyMuted { get; }

    public override string ToString() =>
        $"{CaptureEndpoint.DisplayName} to {RenderEndpoint?.DisplayName ?? "System default output"}";
}

public sealed record AudioMonitorStartResult(
    bool IsSuccess,
    bool IsCancelled,
    Guid SessionId,
    AudioMonitorFailure? Failure)
{
    public static AudioMonitorStartResult Started(Guid sessionId) => new(true, false, sessionId, null);
    public static AudioMonitorStartResult Cancelled(Guid sessionId) => new(false, true, sessionId, null);
    public static AudioMonitorStartResult Failed(Guid sessionId, AudioMonitorFailure failure) => new(false, false, sessionId, failure);

    public override string ToString() => IsSuccess ? "Audio monitoring started" : IsCancelled ? "Audio monitoring cancelled" : Failure?.CustomerMessage ?? "Audio monitoring failed";
}

public sealed record AudioMonitorStopResult(
    bool IsSuccess,
    bool TimedOut,
    AudioMonitorFailure? Failure)
{
    public static AudioMonitorStopResult Stopped { get; } = new(true, false, null);
    public static AudioMonitorStopResult Timeout(AudioMonitorFailure failure) => new(false, true, failure);
    public static AudioMonitorStopResult Failed(AudioMonitorFailure failure) => new(false, false, failure);

    public override string ToString() => IsSuccess ? "Audio monitoring stopped" : Failure?.CustomerMessage ?? "Audio monitoring could not stop";
}

public sealed record AudioEndpointDiscoveryResult(
    IReadOnlyList<AudioEndpoint> CaptureEndpoints,
    IReadOnlyList<AudioEndpoint> RenderEndpoints,
    AudioEndpoint? DefaultRenderEndpoint,
    AudioMonitorFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static AudioEndpointDiscoveryResult Succeeded(
        IReadOnlyList<AudioEndpoint> captureEndpoints,
        IReadOnlyList<AudioEndpoint> renderEndpoints,
        AudioEndpoint? defaultRenderEndpoint) =>
        new(captureEndpoints, renderEndpoints, defaultRenderEndpoint, null);

    public static AudioEndpointDiscoveryResult Failed(AudioMonitorFailure failure) =>
        new([], [], null, failure);
}

public sealed record AudioMonitorDiagnostics(
    Guid SessionId,
    string CaptureEndpointDisplayName,
    string RenderEndpointDisplayName,
    bool UsesSystemDefaultOutput,
    string AdvertisedCaptureFormat,
    string RenderMixFormat,
    AudioStreamFormat CommonFormat,
    AudioMonitorInitializationPath InitializationPath,
    int CapturePeriodFrames,
    int RenderPeriodFrames,
    int CaptureBufferFrames,
    int RenderBufferFrames,
    int CurrentQueueFrames,
    int MaximumQueueFrames,
    long FramesCaptured,
    long FramesRendered,
    long SilentFrames,
    long DiscontinuityCount,
    long TimestampErrorCount,
    long UnderrunCount,
    long OverrunCount,
    long DroppedFrames,
    double CurrentVolumePercent,
    bool IsMuted,
    DateTimeOffset? LastPacketTime,
    DateTimeOffset? LastRenderTime,
    AudioMonitorFailureCategory? LastFailureCategory,
    double? AppliedRateAdjustment = null,
    ulong LastCaptureDevicePosition = 0,
    ulong LastCaptureQpcPosition = 0,
    bool MmcssRegistered = false,
    int RingBufferCapacityFrames = 0,
    int TargetQueueFrames = 0,
    double AverageQueueFrames = 0,
    double? RequestedRateAdjustment = null,
    long RateAdjustmentSaturationMilliseconds = 0,
    long RateAdjustmentDirectionChangeCount = 0,
    IReadOnlyList<AudioDiscontinuityObservation>? DiscontinuityTimeline = null)
{
    public double CurrentQueueMilliseconds => CommonFormat.FramesToMilliseconds(CurrentQueueFrames);
    public double CapturePeriodMilliseconds => CommonFormat.FramesToMilliseconds(CapturePeriodFrames);
    public double RenderPeriodMilliseconds => CommonFormat.FramesToMilliseconds(RenderPeriodFrames);
    public double MaximumQueueMilliseconds => CommonFormat.FramesToMilliseconds(MaximumQueueFrames);

    public override string ToString() =>
        $"Audio {InitializationPath}: {CommonFormat}; queue {CurrentQueueFrames} frames; " +
        $"underruns {UnderrunCount}; overruns {OverrunCount}";
}

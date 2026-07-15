namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

public readonly record struct FullscreenPoint(int X, int Y);

public readonly record struct FullscreenRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => checked(Right - Left);
    public int Height => checked(Bottom - Top);
    public bool IsEmpty => Right <= Left || Bottom <= Top;
}

public readonly record struct FullscreenWindowPlacement(
    uint Flags,
    uint ShowCommand,
    FullscreenPoint MinimumPosition,
    FullscreenPoint MaximumPosition,
    FullscreenRectangle NormalPosition);

public readonly record struct FullscreenNativeStyle(nint Style, nint ExtendedStyle);

/// <summary>
/// Native placement and presentation values captured before fullscreen entry.
/// Rectangle values remain signed physical pixels; the controller never converts
/// them to WPF device-independent units.
/// </summary>
public sealed record FullscreenWindowSnapshot(
    FullscreenWindowPlacement Placement,
    FullscreenNativeStyle NativeStyle,
    int WindowStyle,
    int ResizeMode,
    int WindowState,
    bool Topmost,
    nint MonitorHandle,
    FullscreenRectangle MonitorBounds,
    FullscreenRectangle MonitorWorkArea,
    uint Dpi);

public sealed record FullscreenMonitor(
    nint Handle,
    FullscreenRectangle Bounds,
    FullscreenRectangle WorkArea,
    uint Dpi);

public enum FullscreenExitReason
{
    User,
    Escape,
    Stop,
    PreviewFailure,
    Closing,
    Disposal,
    DisplayRemoved
}

public enum FullscreenTransitionDisposition
{
    Applied,
    NoChange,
    Superseded,
    RolledBack,
    SafeFallback
}

public sealed record FullscreenFailure(
    string CustomerMessage,
    string TechnicalMessage,
    int? HResult = null,
    Exception? Exception = null)
{
    public static FullscreenFailure Unexpected(Exception exception) => new(
        "Fullscreen could not be opened. Live preview is still running.",
        "The fullscreen presentation adapter threw unexpectedly.",
        exception.HResult,
        exception);
}

public sealed record FullscreenTransitionResult(
    bool IsSuccess,
    FullscreenTransitionDisposition Disposition,
    FullscreenFailure? Failure = null)
{
    public bool UsedSafeFallback => Disposition == FullscreenTransitionDisposition.SafeFallback;

    public static FullscreenTransitionResult Applied { get; } = new(true, FullscreenTransitionDisposition.Applied);
    public static FullscreenTransitionResult NoChange { get; } = new(true, FullscreenTransitionDisposition.NoChange);
    public static FullscreenTransitionResult Superseded { get; } = new(true, FullscreenTransitionDisposition.Superseded);

    public static FullscreenTransitionResult Failed(FullscreenFailure failure) =>
        new(false, FullscreenTransitionDisposition.RolledBack, failure);

    public static FullscreenTransitionResult Fallback(FullscreenFailure failure) =>
        new(false, FullscreenTransitionDisposition.SafeFallback, failure);
}

public sealed record FullscreenSnapshotResult(
    FullscreenWindowSnapshot? Snapshot,
    FullscreenFailure? Failure)
{
    public bool IsSuccess => Snapshot is not null && Failure is null;

    public static FullscreenSnapshotResult Captured(FullscreenWindowSnapshot snapshot) => new(snapshot, null);
    public static FullscreenSnapshotResult Failed(FullscreenFailure failure) => new(null, failure);
}

public sealed class FullscreenControllerStateChangedEventArgs(
    bool isFullscreen,
    bool isTransitioning,
    long generation) : EventArgs
{
    public bool IsFullscreen { get; } = isFullscreen;
    public bool IsTransitioning { get; } = isTransitioning;
    public long Generation { get; } = generation;
}

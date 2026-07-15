namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

/// <summary>
/// WPF-aware presentation seam used by the controller. A production adapter can
/// combine Window properties with IWindowNativeApi without exposing either to the
/// view model or capture pipeline.
/// </summary>
public interface IFullscreenWindowAdapter
{
    ValueTask<FullscreenSnapshotResult> CaptureSnapshotAsync(long generation);
    ValueTask<FullscreenTransitionResult> EnterFullscreenAsync(FullscreenWindowSnapshot snapshot, long generation);
    ValueTask<FullscreenTransitionResult> RestoreWindowAsync(
        FullscreenWindowSnapshot snapshot,
        FullscreenExitReason reason,
        long generation);
    ValueTask<FullscreenTransitionResult> ApplySafeWindowedFallbackAsync(
        FullscreenWindowSnapshot? snapshot,
        FullscreenExitReason reason,
        long generation);
}

/// <summary>
/// Native seam intended for a CsWin32-backed production implementation. All
/// coordinates are signed physical pixels, including negative monitor origins.
/// </summary>
public interface IWindowNativeApi
{
    FullscreenSnapshotResult CaptureWindow(nint windowHandle);
    bool TryGetNearestMonitor(nint windowHandle, out FullscreenMonitor monitor, out FullscreenFailure? failure);
    FullscreenTransitionResult ApplyFullscreenBounds(nint windowHandle, FullscreenMonitor monitor);
    FullscreenTransitionResult RestoreWindow(nint windowHandle, FullscreenWindowSnapshot snapshot);
    FullscreenTransitionResult ApplySafeWindowedFallback(
        nint windowHandle,
        FullscreenWindowSnapshot? snapshot,
        FullscreenMonitor nearestMonitor);
}

public interface IFullscreenWindowController : IAsyncDisposable
{
    bool IsFullscreen { get; }
    bool IsTransitioning { get; }
    long Generation { get; }
    event EventHandler<FullscreenControllerStateChangedEventArgs>? StateChanged;
    Task<FullscreenTransitionResult> EnterAsync();
    Task<FullscreenTransitionResult> ExitAsync(FullscreenExitReason reason);
}

public interface IFullscreenInactivityTimer : IDisposable
{
    void Restart(TimeSpan dueTime, Action callback);
    void StopTimer();
}

public interface IFullscreenCursorSink
{
    void SetFullscreenCursorHidden(bool hidden);
}

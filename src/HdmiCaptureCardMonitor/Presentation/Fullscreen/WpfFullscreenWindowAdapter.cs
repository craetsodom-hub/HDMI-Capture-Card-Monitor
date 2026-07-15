using System.Windows;
using System.Windows.Interop;

namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

internal sealed class WpfFullscreenWindowAdapter(
    Window window,
    IWindowNativeApi nativeApi,
    Action reapplyTitleBarTheme) : IFullscreenWindowAdapter
{
    private readonly Window window = window ?? throw new ArgumentNullException(nameof(window));
    private readonly IWindowNativeApi nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    private readonly Action reapplyTitleBarTheme = reapplyTitleBarTheme ?? throw new ArgumentNullException(nameof(reapplyTitleBarTheme));

    public ValueTask<FullscreenSnapshotResult> CaptureSnapshotAsync(long generation)
    {
        _ = generation;
        var native = nativeApi.CaptureWindow(Handle);
        if (!native.IsSuccess) return ValueTask.FromResult(native);
        var enriched = native.Snapshot! with
        {
            WindowStyle = (int)window.WindowStyle,
            ResizeMode = (int)window.ResizeMode,
            WindowState = (int)window.WindowState,
            Topmost = window.Topmost
        };
        return ValueTask.FromResult(FullscreenSnapshotResult.Captured(enriched));
    }

    public ValueTask<FullscreenTransitionResult> EnterFullscreenAsync(FullscreenWindowSnapshot snapshot, long generation)
    {
        _ = generation;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        var monitor = new FullscreenMonitor(snapshot.MonitorHandle, snapshot.MonitorBounds, snapshot.MonitorWorkArea, snapshot.Dpi);
        return ValueTask.FromResult(nativeApi.ApplyFullscreenBounds(Handle, monitor));
    }

    public ValueTask<FullscreenTransitionResult> RestoreWindowAsync(
        FullscreenWindowSnapshot snapshot,
        FullscreenExitReason reason,
        long generation)
    {
        _ = reason;
        _ = generation;
        ApplySavedChrome(snapshot);
        var result = nativeApi.RestoreWindow(Handle, snapshot);
        reapplyTitleBarTheme();
        return ValueTask.FromResult(result);
    }

    public ValueTask<FullscreenTransitionResult> ApplySafeWindowedFallbackAsync(
        FullscreenWindowSnapshot? snapshot,
        FullscreenExitReason reason,
        long generation)
    {
        _ = reason;
        _ = generation;
        if (snapshot is not null) ApplySavedChrome(snapshot);
        else
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.ResizeMode = ResizeMode.CanResize;
            window.Topmost = false;
        }

        FullscreenTransitionResult result;
        if (nativeApi.TryGetNearestMonitor(Handle, out var monitor, out var failure))
            result = nativeApi.ApplySafeWindowedFallback(Handle, snapshot, monitor);
        else
            result = FullscreenTransitionResult.Failed(failure is null
                ? FullscreenFailure.Unexpected(
                    FullscreenOperation.SafeFallback,
                    new InvalidOperationException("No monitor is available."))
                : FullscreenFailure.Create(
                    FullscreenOperation.SafeFallback,
                    failure.TechnicalMessage,
                    failure.NativeError,
                    failure.Exception));
        reapplyTitleBarTheme();
        return ValueTask.FromResult(result);
    }

    private nint Handle => new WindowInteropHelper(window).Handle;

    private void ApplySavedChrome(FullscreenWindowSnapshot snapshot)
    {
        window.WindowState = WindowState.Normal;
        window.WindowStyle = (WindowStyle)snapshot.WindowStyle;
        window.ResizeMode = (ResizeMode)snapshot.ResizeMode;
        window.Topmost = snapshot.Topmost;
    }
}

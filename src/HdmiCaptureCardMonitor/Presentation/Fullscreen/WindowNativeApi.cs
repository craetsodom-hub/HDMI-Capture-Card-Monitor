using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

internal sealed unsafe class WindowNativeApi : IWindowNativeApi
{
    private readonly NativeWindowStyleAccessor styleAccessor;

    public WindowNativeApi() : this(new CsWin32WindowStyleInterop()) { }

    internal WindowNativeApi(IWindowStyleInterop styleInterop) =>
        styleAccessor = new NativeWindowStyleAccessor(styleInterop);

    public FullscreenSnapshotResult CaptureWindow(nint windowHandle)
    {
        if (windowHandle == 0) return FullscreenSnapshotResult.Failed(Failure(FullscreenOperation.SnapshotCapture, "The application window is not available."));
        var window = new HWND((void*)windowHandle);
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!PInvoke.GetWindowPlacement(window, &placement))
            return FullscreenSnapshotResult.Failed(Failure(FullscreenOperation.SnapshotCapture, "GetWindowPlacement failed."));
        if (!TryGetNearestMonitor(windowHandle, out var monitor, out var monitorFailure))
            return FullscreenSnapshotResult.Failed(monitorFailure ?? Failure(FullscreenOperation.SnapshotCapture, "Monitor selection failed."));

        var style = styleAccessor.Read(windowHandle, NativeWindowStyleKind.Style);
        if (!style.IsSuccess)
            return FullscreenSnapshotResult.Failed(Failure(
                FullscreenOperation.SnapshotCapture,
                "GetWindowLongPtr could not read the normal window style.",
                style.ErrorCode));
        var extendedStyle = styleAccessor.Read(windowHandle, NativeWindowStyleKind.ExtendedStyle);
        if (!extendedStyle.IsSuccess)
            return FullscreenSnapshotResult.Failed(Failure(
                FullscreenOperation.SnapshotCapture,
                "GetWindowLongPtr could not read the extended window style.",
                extendedStyle.ErrorCode));

        var snapshot = new FullscreenWindowSnapshot(
            ToPlacement(placement),
            new FullscreenNativeStyle(style.Value, extendedStyle.Value),
            0,
            0,
            0,
            false,
            monitor.Handle,
            monitor.Bounds,
            monitor.WorkArea,
            monitor.Dpi);
        return FullscreenSnapshotResult.Captured(snapshot);
    }

    public bool TryGetNearestMonitor(nint windowHandle, out FullscreenMonitor monitor, out FullscreenFailure? failure)
    {
        monitor = default!;
        failure = null;
        if (windowHandle == 0)
        {
            failure = Failure(FullscreenOperation.SnapshotCapture, "The application window is not available for monitor selection.");
            return false;
        }

        var nativeMonitor = PInvoke.MonitorFromWindow(
            new HWND((void*)windowHandle),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (nativeMonitor.Value is null)
        {
            failure = Failure(FullscreenOperation.SnapshotCapture, "MonitorFromWindow returned no monitor.");
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(nativeMonitor, &info))
        {
            failure = Failure(FullscreenOperation.SnapshotCapture, "GetMonitorInfo failed.");
            return false;
        }

        var dpi = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)
            ? PInvoke.GetDpiForWindow(new HWND((void*)windowHandle))
            : 96u;
        monitor = new FullscreenMonitor(
            (nint)nativeMonitor.Value,
            ToRectangle(info.rcMonitor),
            ToRectangle(info.rcWork),
            dpi == 0 ? 96u : dpi);
        return true;
    }

    public FullscreenTransitionResult ApplyFullscreenBounds(nint windowHandle, FullscreenMonitor monitor)
    {
        if (windowHandle == 0 || monitor.Bounds.IsEmpty)
            return FullscreenTransitionResult.Failed(Failure(FullscreenOperation.Entry, "Fullscreen monitor bounds are unavailable."));
        var bounds = monitor.Bounds;
        var applied = PInvoke.SetWindowPos(
            new HWND((void*)windowHandle),
            default,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        return applied
            ? FullscreenTransitionResult.Applied
            : FullscreenTransitionResult.Failed(Failure(FullscreenOperation.Entry, "SetWindowPos could not apply fullscreen bounds."));
    }

    public FullscreenTransitionResult RestoreWindow(nint windowHandle, FullscreenWindowSnapshot snapshot)
    {
        if (windowHandle == 0)
            return FullscreenTransitionResult.Failed(Failure(FullscreenOperation.ExactRestore, "The application window is unavailable for restoration."));
        var window = new HWND((void*)windowHandle);
        var restored = WindowRestorationResult.Execute(
            () => styleAccessor.Write(windowHandle, NativeWindowStyleKind.Style, snapshot.NativeStyle.Style),
            () => styleAccessor.Write(windowHandle, NativeWindowStyleKind.ExtendedStyle, snapshot.NativeStyle.ExtendedStyle),
            () => RestorePlacement(window, snapshot.Placement),
            () => PInvoke.SetWindowPos(
                window,
                default,
                0,
                0,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER));
        return restored.IsSuccess
            ? FullscreenTransitionResult.Applied
            : FullscreenTransitionResult.Failed(Failure(
                FullscreenOperation.ExactRestore,
                $"Exact window restoration failed for: {restored.FailureSummary}.",
                restored.FirstNativeError));
    }

    public FullscreenTransitionResult ApplySafeWindowedFallback(
        nint windowHandle,
        FullscreenWindowSnapshot? snapshot,
        FullscreenMonitor nearestMonitor)
    {
        if (windowHandle == 0 || nearestMonitor.WorkArea.IsEmpty)
            return FullscreenTransitionResult.Failed(Failure(FullscreenOperation.SafeFallback, "No visible work area is available for a safe fallback."));
        var work = nearestMonitor.WorkArea;
        var requested = snapshot?.Placement.NormalPosition;
        var width = Math.Min(requested?.Width ?? 1180, work.Width);
        var height = Math.Min(requested?.Height ?? 780, work.Height);
        var left = work.Left + Math.Max(0, (work.Width - width) / 2);
        var top = work.Top + Math.Max(0, (work.Height - height) / 2);
        var applied = PInvoke.SetWindowPos(
            new HWND((void*)windowHandle),
            default,
            left,
            top,
            width,
            height,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        return applied
            ? FullscreenTransitionResult.Applied
            : FullscreenTransitionResult.Failed(Failure(FullscreenOperation.SafeFallback, "A visible fallback window could not be placed."));
    }

    private static FullscreenWindowPlacement ToPlacement(WINDOWPLACEMENT placement) => new(
        (uint)placement.flags,
        (uint)placement.showCmd,
        new FullscreenPoint(placement.ptMinPosition.X, placement.ptMinPosition.Y),
        new FullscreenPoint(placement.ptMaxPosition.X, placement.ptMaxPosition.Y),
        ToRectangle(placement.rcNormalPosition));

    private static WINDOWPLACEMENT ToNativePlacement(FullscreenWindowPlacement placement) => new()
    {
        length = (uint)sizeof(WINDOWPLACEMENT),
        flags = (WINDOWPLACEMENT_FLAGS)placement.Flags,
        showCmd = (SHOW_WINDOW_CMD)placement.ShowCommand,
        ptMinPosition = new() { X = placement.MinimumPosition.X, Y = placement.MinimumPosition.Y },
        ptMaxPosition = new() { X = placement.MaximumPosition.X, Y = placement.MaximumPosition.Y },
        rcNormalPosition = new RECT(
            placement.NormalPosition.Left,
            placement.NormalPosition.Top,
            placement.NormalPosition.Right,
            placement.NormalPosition.Bottom)
    };

    private static bool RestorePlacement(HWND window, FullscreenWindowPlacement savedPlacement)
    {
        var placement = ToNativePlacement(savedPlacement);
        return PInvoke.SetWindowPlacement(window, &placement);
    }

    private static FullscreenRectangle ToRectangle(RECT rectangle) =>
        new(rectangle.left, rectangle.top, rectangle.right, rectangle.bottom);

    private static FullscreenFailure Failure(
        FullscreenOperation operation,
        string technicalMessage,
        uint? nativeError = null) =>
        FullscreenFailure.Create(operation, technicalMessage, nativeError);
}

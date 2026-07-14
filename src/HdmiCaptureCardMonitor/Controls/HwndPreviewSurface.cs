using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Rendering;
using HdmiCaptureCardMonitor.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace HdmiCaptureCardMonitor.Controls;

public sealed unsafe class HwndPreviewSurface : HwndHost, IPreviewSurface
{
    private nint childWindow;
    private PreviewSurfaceSize pixelSize;
    private bool videoVisible;
    private bool windowMinimized;
    private bool lastPublishedPresentability;

    internal static WINDOW_STYLE ChildWindowStyle =>
        WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_CLIPSIBLINGS | WINDOW_STYLE.WS_CLIPCHILDREN;

    nint IPreviewSurface.Handle => childWindow;
    public PreviewSurfaceSize PixelSize => pixelSize;
    public bool IsAvailable => childWindow != 0;
    public bool IsPresentable => IsAvailable && !windowMinimized && !pixelSize.IsEmpty;
    public bool IsVideoVisible => videoVisible;

    public event EventHandler<PreviewSurfaceSize>? PixelSizeChanged;
    public event EventHandler? AvailabilityChanged;
    public event EventHandler? PresentabilityChanged;

    public void SetSurfaceActive(bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetSurfaceActive(active));
            return;
        }

        if (!active) videoVisible = false;
        Visibility = active ? Visibility.Visible : Visibility.Hidden;
        ApplyNativeVisibility();
        PublishPresentabilityIfChanged();
    }

    public void SetVideoVisible(bool visible)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetVideoVisible(visible));
            return;
        }

        videoVisible = visible;
        ApplyNativeVisibility();
    }

    public void SetWindowMinimized(bool minimized)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetWindowMinimized(minimized));
            return;
        }

        windowMinimized = minimized;
        ApplyNativeVisibility();
        PublishPresentabilityIfChanged();
    }

    protected override unsafe HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var window = PInvoke.CreateWindowEx(
            default,
            "STATIC",
            string.Empty,
            ChildWindowStyle,
            -32000,
            -32000,
            1,
            1,
            new HWND(hwndParent.Handle),
            null,
            null,
            null);

        if (window.Value is null) throw new InvalidOperationException("The native preview surface could not be created.");
        childWindow = (nint)window.Value;
        _ = PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_HIDE);
        // HwndHost performs its own post-build positioning/show work. Reassert the
        // requested hidden state after that work without collapsing the stable host.
        _ = Dispatcher.BeginInvoke(() => SetSurfaceActive(false));
        UpdatePixelSize();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        PublishPresentabilityIfChanged();
        return new HandleRef(this, childWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        var window = new HWND(hwnd.Handle);
        if (window.Value is not null) _ = PInvoke.DestroyWindow(window);
        childWindow = 0;
        videoVisible = false;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        PublishPresentabilityIfChanged();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePixelSize();
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        // WPF may show an HwndHost child again while arranging it. Preserve the
        // stable HWND but immediately re-hide it until the first frame is ready.
        if (childWindow != 0 && !videoVisible) ApplyNativeVisibility();
    }

    private void UpdatePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var next = DpiPixelSizeConverter.Convert(ActualWidth, ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        if (next == pixelSize) return;
        pixelSize = next;

        if (childWindow != 0 && videoVisible && !next.IsEmpty)
        {
            _ = PInvoke.SetWindowPos(
                new HWND((void*)childWindow),
                default,
                0,
                0,
                next.PixelWidth,
                next.PixelHeight,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        }

        PixelSizeChanged?.Invoke(this, next);
        PublishPresentabilityIfChanged();
    }

    private void ApplyNativeVisibility()
    {
        if (childWindow == 0) return;
        var window = new HWND((void*)childWindow);
        if (videoVisible && IsPresentable)
        {
            if (!pixelSize.IsEmpty)
            {
                _ = PInvoke.SetWindowPos(window, default, 0, 0, pixelSize.PixelWidth, pixelSize.PixelHeight,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
            }
            _ = PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNA);
            return;
        }

        _ = PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_HIDE);
        _ = PInvoke.SetWindowPos(window, default, -32000, -32000, 1, 1,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    private void PublishPresentabilityIfChanged()
    {
        var presentable = IsPresentable;
        if (presentable == lastPublishedPresentability) return;
        lastPublishedPresentability = presentable;
        PresentabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}

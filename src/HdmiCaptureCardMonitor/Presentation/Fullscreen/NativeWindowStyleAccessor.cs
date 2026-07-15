using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

internal enum NativeWindowStyleKind
{
    Style,
    ExtendedStyle
}

internal readonly record struct NativeStyleAccessResult(
    bool IsSuccess,
    nint Value,
    uint ErrorCode);

internal interface IWindowStyleInterop
{
    uint NoErrorCode { get; }
    void ClearLastError();
    uint GetLastError();
    nint GetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind);
    nint SetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind, nint value);
}

internal sealed class NativeWindowStyleAccessor(IWindowStyleInterop interop)
{
    private readonly IWindowStyleInterop interop = interop ?? throw new ArgumentNullException(nameof(interop));

    public NativeStyleAccessResult Read(nint windowHandle, NativeWindowStyleKind kind) =>
        Execute(() => interop.GetWindowLongPtr(windowHandle, kind));

    public NativeStyleAccessResult Write(nint windowHandle, NativeWindowStyleKind kind, nint value) =>
        Execute(() => interop.SetWindowLongPtr(windowHandle, kind, value));

    private NativeStyleAccessResult Execute(Func<nint> operation)
    {
        interop.ClearLastError();
        var value = operation();
        if (value != 0) return new NativeStyleAccessResult(true, value, interop.NoErrorCode);

        var error = interop.GetLastError();
        return new NativeStyleAccessResult(error == interop.NoErrorCode, value, error);
    }
}

internal sealed unsafe class CsWin32WindowStyleInterop : IWindowStyleInterop
{
    public uint NoErrorCode => (uint)WIN32_ERROR.NO_ERROR;

    public void ClearLastError() => PInvoke.SetLastError(WIN32_ERROR.NO_ERROR);

    public uint GetLastError() => unchecked((uint)Marshal.GetLastWin32Error());

    public nint GetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind) =>
        PInvoke.GetWindowLongPtr(new HWND((void*)windowHandle), ToIndex(kind));

    public nint SetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind, nint value) =>
        PInvoke.SetWindowLongPtr(new HWND((void*)windowHandle), ToIndex(kind), value);

    private static WINDOW_LONG_PTR_INDEX ToIndex(NativeWindowStyleKind kind) => kind switch
    {
        NativeWindowStyleKind.Style => WINDOW_LONG_PTR_INDEX.GWL_STYLE,
        NativeWindowStyleKind.ExtendedStyle => WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal sealed record WindowRestorationResult(
    bool IsSuccess,
    bool StyleRestored,
    bool ExtendedStyleRestored,
    bool PlacementRestored,
    bool FrameRefreshed,
    uint? FirstNativeError)
{
    public static WindowRestorationResult Execute(
        Func<NativeStyleAccessResult> restoreStyle,
        Func<NativeStyleAccessResult> restoreExtendedStyle,
        Func<bool> restorePlacement,
        Func<bool> refreshFrame)
    {
        var style = restoreStyle();
        var extendedStyle = restoreExtendedStyle();
        var placement = restorePlacement();
        var frame = refreshFrame();
        var success = style.IsSuccess && extendedStyle.IsSuccess && placement && frame;
        uint? firstNativeError = !style.IsSuccess
            ? style.ErrorCode
            : !extendedStyle.IsSuccess
                ? extendedStyle.ErrorCode
                : (uint?)null;
        return new WindowRestorationResult(
            success,
            style.IsSuccess,
            extendedStyle.IsSuccess,
            placement,
            frame,
            firstNativeError);
    }

    public string FailureSummary
    {
        get
        {
            var failures = new List<string>(4);
            if (!StyleRestored) failures.Add("normal style");
            if (!ExtendedStyleRestored) failures.Add("extended style");
            if (!PlacementRestored) failures.Add("window placement");
            if (!FrameRefreshed) failures.Add("frame refresh");
            return string.Join(", ", failures);
        }
    }
}

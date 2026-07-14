using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Rendering;

public static class DpiPixelSizeConverter
{
    public static PreviewSurfaceSize Convert(double logicalWidth, double logicalHeight, double dpiScaleX, double dpiScaleY)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0 || dpiScaleX <= 0 || dpiScaleY <= 0) return default;
        return new PreviewSurfaceSize(
            Math.Max(1, (int)Math.Round(logicalWidth * dpiScaleX)),
            Math.Max(1, (int)Math.Round(logicalHeight * dpiScaleY)));
    }
}

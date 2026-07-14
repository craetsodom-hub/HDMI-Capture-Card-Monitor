namespace HdmiCaptureCardMonitor.Models;

public readonly record struct PreviewSurfaceSize(int PixelWidth, int PixelHeight)
{
    public bool IsEmpty => PixelWidth <= 0 || PixelHeight <= 0;
}

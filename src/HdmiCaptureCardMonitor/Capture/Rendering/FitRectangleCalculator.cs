namespace HdmiCaptureCardMonitor.Capture.Rendering;

public static class FitRectangleCalculator
{
    public static PixelRectangle Calculate(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0) return default;

        var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        var width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, targetWidth);
        var height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, targetHeight);
        return new PixelRectangle((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }
}

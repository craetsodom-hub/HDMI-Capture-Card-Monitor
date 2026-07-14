namespace HdmiCaptureCardMonitor.Capture.Rendering;

public readonly record struct PixelRectangle(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

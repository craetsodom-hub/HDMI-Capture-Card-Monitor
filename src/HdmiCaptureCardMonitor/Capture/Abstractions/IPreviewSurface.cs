using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Abstractions;

public interface IPreviewSurface
{
    nint Handle { get; }
    PreviewSurfaceSize PixelSize { get; }
    bool IsAvailable { get; }
    bool IsPresentable { get; }
    bool IsVideoVisible { get; }
    event EventHandler<PreviewSurfaceSize>? PixelSizeChanged;
    event EventHandler? AvailabilityChanged;
    event EventHandler? PresentabilityChanged;
    void SetSurfaceActive(bool active);
    void SetVideoVisible(bool visible);
}

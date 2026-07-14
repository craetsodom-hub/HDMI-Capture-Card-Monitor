using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Rendering;

internal sealed class ResizeRequestMailbox
{
    private readonly object syncRoot = new();
    private PreviewSurfaceSize pending;
    private bool hasPending;

    public static int Capacity => 1;

    public void Post(PreviewSurfaceSize size)
    {
        lock (syncRoot)
        {
            pending = size;
            hasPending = true;
        }
    }

    public bool TryTake(out PreviewSurfaceSize size)
    {
        lock (syncRoot)
        {
            size = pending;
            if (!hasPending) return false;
            hasPending = false;
            return true;
        }
    }
}

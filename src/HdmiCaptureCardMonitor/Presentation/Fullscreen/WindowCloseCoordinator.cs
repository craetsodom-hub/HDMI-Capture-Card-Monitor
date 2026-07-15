namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

internal enum WindowCloseDecision
{
    AllowAndDispose,
    CancelAndPrepare,
    CancelWhilePreparing
}

/// <summary>
/// Keeps WPF Closing synchronous while allowing only a fullscreen or in-flight
/// presentation change to take the one asynchronous preparation path.
/// </summary>
internal sealed class WindowCloseCoordinator
{
    private readonly object syncRoot = new();
    private bool preparing;
    private bool prepared;
    private bool reissueRequested;
    private bool disposed;

    public WindowCloseDecision Evaluate(bool isFullscreen, bool isTransitioning)
    {
        lock (syncRoot)
        {
            if (disposed || prepared) return WindowCloseDecision.AllowAndDispose;
            if (preparing) return WindowCloseDecision.CancelWhilePreparing;
            if (!isFullscreen && !isTransitioning) return WindowCloseDecision.AllowAndDispose;

            preparing = true;
            return WindowCloseDecision.CancelAndPrepare;
        }
    }

    public bool CompletePreparationAndRequestClose()
    {
        lock (syncRoot)
        {
            if (!preparing || reissueRequested) return false;
            preparing = false;
            prepared = true;
            reissueRequested = true;
            return true;
        }
    }

    public void MarkDisposed()
    {
        lock (syncRoot) disposed = true;
    }
}

internal static class FullscreenDisplayChangePolicy
{
    public static bool ShouldRequestExit(bool isDisposed, bool isFullscreen, bool isTransitioning) =>
        !isDisposed && (isFullscreen || isTransitioning);
}

using System.Windows.Threading;

namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

internal sealed class DispatcherFullscreenInactivityTimer : IFullscreenInactivityTimer
{
    private readonly DispatcherTimer timer;
    private Action? callback;
    private bool disposed;

    internal DispatcherFullscreenInactivityTimer(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
        timer.Tick += OnTick;
    }

    public void Restart(TimeSpan dueTime, Action callback)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        timer.Stop();
        this.callback = callback;
        timer.Interval = dueTime;
        timer.Start();
    }

    public void StopTimer()
    {
        if (disposed) return;
        timer.Stop();
        callback = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= OnTick;
        callback = null;
        GC.SuppressFinalize(this);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ = sender;
        timer.Stop();
        var pending = callback;
        callback = null;
        pending?.Invoke();
    }
}

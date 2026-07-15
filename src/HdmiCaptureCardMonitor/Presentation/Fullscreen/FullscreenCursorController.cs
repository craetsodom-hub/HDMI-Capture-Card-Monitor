namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

/// <summary>
/// Event-driven cursor inactivity policy. The injected timer must not invoke its
/// callback synchronously from Restart; production callbacks should be dispatched
/// to the cursor sink's owning UI context.
/// </summary>
public sealed class FullscreenCursorController : IDisposable
{
    public static TimeSpan DefaultInactivityDelay { get; } = TimeSpan.FromSeconds(2);

    private readonly object syncRoot = new();
    private readonly IFullscreenInactivityTimer timer;
    private readonly IFullscreenCursorSink sink;
    private readonly TimeSpan inactivityDelay;
    private bool active;
    private bool transitioning;
    private bool hidden;
    private bool disposed;
    private long timerGeneration;

    public FullscreenCursorController(
        IFullscreenInactivityTimer timer,
        IFullscreenCursorSink sink,
        TimeSpan? inactivityDelay = null)
    {
        this.timer = timer ?? throw new ArgumentNullException(nameof(timer));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.inactivityDelay = inactivityDelay ?? DefaultInactivityDelay;
        if (this.inactivityDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityDelay));
    }

    public bool IsHidden { get { lock (syncRoot) return hidden; } }

    public void EnterFullscreenPreview()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            active = true;
            SetHiddenLocked(false, force: true);
            ArmLocked();
        }
    }

    public void NotifyPointerActivity()
    {
        lock (syncRoot)
        {
            if (disposed || !active) return;
            SetHiddenLocked(false);
            if (!transitioning) ArmLocked();
        }
    }

    public void SetTransitioning(bool value)
    {
        lock (syncRoot)
        {
            if (disposed) return;
            transitioning = value;
            timerGeneration++;
            timer.StopTimer();
            SetHiddenLocked(false, force: value);
            if (!value && active) ArmLocked();
        }
    }

    public void ExitFullscreen()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            active = false;
            transitioning = false;
            timerGeneration++;
            timer.StopTimer();
            SetHiddenLocked(false, force: true);
        }
    }

    public void RestoreForFailure() => ExitFullscreen();

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
            active = false;
            transitioning = false;
            timerGeneration++;
            timer.StopTimer();
            SetHiddenLocked(false, force: true);
            timer.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private void ArmLocked()
    {
        if (!active || transitioning || disposed) return;
        var generation = ++timerGeneration;
        timer.Restart(inactivityDelay, () => OnTimerElapsed(generation));
    }

    private void OnTimerElapsed(long generation)
    {
        lock (syncRoot)
        {
            if (disposed || !active || transitioning || generation != timerGeneration) return;
            SetHiddenLocked(true);
        }
    }

    private void SetHiddenLocked(bool value, bool force = false)
    {
        if (!force && hidden == value) return;
        hidden = value;
        sink.SetFullscreenCursorHidden(value);
    }
}

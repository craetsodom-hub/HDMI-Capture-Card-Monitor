using System.Diagnostics;
using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal static class SafeAudioEventDispatch
{
    internal static void Publish<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs eventArgs,
        IApplicationLogger logger,
        string eventName)
        where TEventArgs : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(sender, eventArgs); }
            catch (Exception exception)
            {
                try { logger.Warning($"An audio {eventName} observer failed safely ({exception.GetType().Name})."); }
                catch (Exception) { }
            }
        }
    }
}

/// <summary>
/// Capacity-one, non-real-time publisher. Producers replace the pending value;
/// subscribers run only on this managed publisher thread and at most twice per second.
/// </summary>
internal sealed class LatestAudioDiagnosticsPublisher : IDisposable
{
    private const int MinimumPublishIntervalMilliseconds = 500;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<AudioMonitorDiagnosticsEventArgs> publish;
    private readonly AutoResetEvent updated = new(false);
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AudioMonitorDiagnosticsEventArgs? latest;
    private int started;
    private int stopRequested;
    private int handlesDisposed;

    internal LatestAudioDiagnosticsPublisher(Action<AudioMonitorDiagnosticsEventArgs> publish)
    {
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Audio diagnostics publisher",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal Task Completion => completion.Task;

    internal void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        thread.Start();
    }

    internal void PublishLatest(AudioMonitorDiagnosticsEventArgs value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Volatile.Read(ref stopRequested) != 0) return;
        Interlocked.Exchange(ref latest, value);
        updated.Set();
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0) stop.Set();
        if (Volatile.Read(ref started) == 0)
        {
            completion.TrySetResult();
            DisposeHandles();
            return true;
        }

        if (!completion.Task.Wait(StopTimeout)) return false;
        DisposeHandles();
        return true;
    }

    public void Dispose() => _ = Stop();

    private void ThreadMain()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            long lastPublishedAt = -MinimumPublishIntervalMilliseconds;
            var waits = new WaitHandle[] { stop, updated };
            while (true)
            {
                var signaled = WaitHandle.WaitAny(waits);
                if (signaled == 0) break;

                var remaining = MinimumPublishIntervalMilliseconds - (clock.ElapsedMilliseconds - lastPublishedAt);
                if (remaining > 0 && stop.WaitOne(checked((int)remaining))) break;
                var value = Interlocked.Exchange(ref latest, null);
                if (value is null) continue;
                try { publish(value); }
                catch (Exception) { }
                lastPublishedAt = clock.ElapsedMilliseconds;
            }
        }
        finally
        {
            Interlocked.Exchange(ref latest, null);
            completion.TrySetResult();
        }
    }

    private void DisposeHandles()
    {
        if (Interlocked.Exchange(ref handlesDisposed, 1) != 0) return;
        updated.Dispose();
        stop.Dispose();
    }
}

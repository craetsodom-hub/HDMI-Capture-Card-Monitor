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

internal sealed class AudioControlEventPublisher : IDisposable
{
    private const int DefaultCapacity = 8;
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<AudioMonitorStateChangedEventArgs> publishState;
    private readonly Action<AudioMonitorFailureEventArgs> publishFailure;
    private readonly int capacity;
    private readonly TimeSpan stopTimeout;
    private readonly object syncRoot = new();
    private readonly Queue<ControlEvent> pending = new();
    private readonly AutoResetEvent updated = new(false);
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int started;
    private int stopRequested;
    private int handlesDisposed;

    internal AudioControlEventPublisher(
        Action<AudioMonitorStateChangedEventArgs> publishState,
        Action<AudioMonitorFailureEventArgs> publishFailure,
        int capacity = DefaultCapacity,
        TimeSpan? stopTimeout = null)
    {
        this.publishState = publishState ?? throw new ArgumentNullException(nameof(publishState));
        this.publishFailure = publishFailure ?? throw new ArgumentNullException(nameof(publishFailure));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        this.capacity = capacity;
        this.stopTimeout = stopTimeout ?? DefaultStopTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(this.stopTimeout, TimeSpan.Zero);
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Audio control-event publisher",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal Task Completion => completion.Task;
    internal bool HandlesReleased => Volatile.Read(ref handlesDisposed) != 0;

    internal void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        thread.Start();
    }

    internal bool PublishState(AudioMonitorStateChangedEventArgs value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (syncRoot)
        {
            if (Volatile.Read(ref stopRequested) != 0) return false;
            if (pending.LastOrDefault() is { Kind: ControlEventKind.State, State: { } last } &&
                last.CurrentState == value.CurrentState)
                return true;

            if (pending.Count >= capacity && !RemoveOldestState()) return false;
            pending.Enqueue(ControlEvent.ForState(value));
        }
        SignalUpdated();
        return true;
    }

    internal bool PublishFailure(AudioMonitorFailureEventArgs value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (syncRoot)
        {
            if (Volatile.Read(ref stopRequested) != 0) return false;
            while (pending.Count >= capacity && !RemoveOldestState())
            {
                Monitor.Wait(syncRoot);
                if (Volatile.Read(ref stopRequested) != 0) return false;
            }
            pending.Enqueue(ControlEvent.ForFailure(value));
        }
        SignalUpdated();
        return true;
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0)
        {
            lock (syncRoot) Monitor.PulseAll(syncRoot);
            SignalStop();
        }
        if (Volatile.Read(ref started) == 0)
        {
            lock (syncRoot) pending.Clear();
            DisposeHandles();
            completion.TrySetResult();
            return true;
        }
        return completion.Task.Wait(stopTimeout);
    }

    public void Dispose() => _ = Stop();

    private void ThreadMain()
    {
        try
        {
            var waits = new WaitHandle[] { stop, updated };
            while (true)
            {
                ControlEvent next;
                lock (syncRoot)
                {
                    if (pending.Count > 0)
                    {
                        next = pending.Dequeue();
                        Monitor.PulseAll(syncRoot);
                    }
                    else
                    {
                        if (Volatile.Read(ref stopRequested) != 0) break;
                        next = default;
                    }
                }

                if (next.Kind == ControlEventKind.None)
                {
                    _ = WaitHandle.WaitAny(waits);
                    continue;
                }

                try
                {
                    if (next.Kind == ControlEventKind.State) publishState(next.State!);
                    else publishFailure(next.Failure!);
                }
                catch (Exception) { }
            }
        }
        finally
        {
            lock (syncRoot)
            {
                pending.Clear();
                Monitor.PulseAll(syncRoot);
            }
            DisposeHandles();
            completion.TrySetResult();
        }
    }

    private bool RemoveOldestState()
    {
        if (!pending.Any(item => item.Kind == ControlEventKind.State)) return false;
        var retained = new Queue<ControlEvent>(pending.Count - 1);
        var removed = false;
        while (pending.TryDequeue(out var item))
        {
            if (!removed && item.Kind == ControlEventKind.State) removed = true;
            else retained.Enqueue(item);
        }
        while (retained.TryDequeue(out var item)) pending.Enqueue(item);
        return removed;
    }

    private void SignalUpdated()
    {
        try { updated.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void SignalStop()
    {
        try { stop.Set(); }
        catch (ObjectDisposedException) { }
        SignalUpdated();
    }

    private void DisposeHandles()
    {
        if (Interlocked.Exchange(ref handlesDisposed, 1) != 0) return;
        updated.Dispose();
        stop.Dispose();
    }

    private enum ControlEventKind { None, State, Failure }

    private readonly record struct ControlEvent(
        ControlEventKind Kind,
        AudioMonitorStateChangedEventArgs? State,
        AudioMonitorFailureEventArgs? Failure)
    {
        internal static ControlEvent ForState(AudioMonitorStateChangedEventArgs value) =>
            new(ControlEventKind.State, value, null);
        internal static ControlEvent ForFailure(AudioMonitorFailureEventArgs value) =>
            new(ControlEventKind.Failure, null, value);
    }
}

/// <summary>
/// Capacity-one, non-real-time publisher. Producers replace the pending value;
/// subscribers run only on this managed publisher thread and at most twice per second.
/// </summary>
internal sealed class LatestAudioDiagnosticsPublisher : IDisposable
{
    private const int MinimumPublishIntervalMilliseconds = 500;
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<AudioMonitorDiagnosticsEventArgs> publish;
    private readonly AutoResetEvent updated = new(false);
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly TimeSpan stopTimeout;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AudioMonitorDiagnosticsEventArgs? latest;
    private int started;
    private int stopRequested;
    private int handlesDisposed;

    internal LatestAudioDiagnosticsPublisher(
        Action<AudioMonitorDiagnosticsEventArgs> publish,
        TimeSpan? stopTimeout = null)
    {
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.stopTimeout = stopTimeout ?? DefaultStopTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(this.stopTimeout, TimeSpan.Zero);
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Audio diagnostics publisher",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal Task Completion => completion.Task;
    internal bool HandlesReleased => Volatile.Read(ref handlesDisposed) != 0;

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
        try { updated.Set(); }
        catch (ObjectDisposedException) { }
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0)
        {
            try { stop.Set(); }
            catch (ObjectDisposedException) { }
        }
        if (Volatile.Read(ref started) == 0)
        {
            completion.TrySetResult();
            DisposeHandles();
            return true;
        }

        return completion.Task.Wait(stopTimeout);
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
            DisposeHandles();
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

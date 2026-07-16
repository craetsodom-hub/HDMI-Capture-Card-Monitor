using System.Diagnostics;
using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal readonly record struct AudioEventTimingSnapshot(
    long EventCount,
    double AverageMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    long LongGapCount);

internal readonly record struct AudioCaptureWorkerSnapshot(
    AudioEventTimingSnapshot EventTiming,
    IReadOnlyDictionary<int, long> PacketFrameDistribution,
    long EmptyWakeCount,
    long FramesCaptured,
    long SilentFrames,
    long DiscontinuityCount,
    long TimestampErrorCount,
    ulong LastDevicePosition,
    ulong LastQpcPosition,
    DateTimeOffset? LastPacketTime,
    bool MmcssRegistered);

internal readonly record struct AudioRenderWorkerSnapshot(
    AudioEventTimingSnapshot EventTiming,
    IReadOnlyDictionary<int, long> FramesAvailableDistribution,
    uint CurrentPaddingFrames,
    double AverageFramesAvailable,
    int MaximumFramesAvailable,
    long LateWakeCount,
    long NativeUnderfillEvents,
    long FramesRequested,
    long LiveFramesRendered,
    long SilenceInsertedFrames,
    DateTimeOffset? LastRenderTime,
    bool MmcssRegistered);

internal sealed class AudioEventTimingStatistics
{
    private const int MaximumBucketMilliseconds = 1000;
    private readonly long[] buckets = new long[MaximumBucketMilliseconds + 2];
    private readonly double longGapMilliseconds;
    private long lastTimestamp;
    private long eventCount;
    private long intervalCount;
    private long intervalTicks;
    private long maximumTicks;
    private long longGapCount;

    internal AudioEventTimingStatistics(double longGapMilliseconds) =>
        this.longGapMilliseconds = longGapMilliseconds;

    internal void Record(long timestamp)
    {
        Interlocked.Increment(ref eventCount);
        var previous = Interlocked.Exchange(ref lastTimestamp, timestamp);
        if (previous == 0) return;
        var ticks = timestamp - previous;
        Interlocked.Increment(ref intervalCount);
        Interlocked.Add(ref intervalTicks, ticks);
        UpdateMaximum(ref maximumTicks, ticks);
        var milliseconds = ticks * 1000d / Stopwatch.Frequency;
        if (milliseconds > longGapMilliseconds) Interlocked.Increment(ref longGapCount);
        var bucket = Math.Clamp((int)Math.Ceiling(milliseconds), 0, MaximumBucketMilliseconds + 1);
        Interlocked.Increment(ref buckets[bucket]);
    }

    internal AudioEventTimingSnapshot Snapshot()
    {
        var intervals = Interlocked.Read(ref intervalCount);
        var average = intervals == 0
            ? 0
            : Interlocked.Read(ref intervalTicks) * 1000d / Stopwatch.Frequency / intervals;
        var target = intervals == 0 ? 0 : checked((long)Math.Ceiling(intervals * 0.95));
        long cumulative = 0;
        var p95 = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            cumulative += Interlocked.Read(ref buckets[index]);
            if (cumulative < target) continue;
            p95 = index;
            break;
        }
        return new AudioEventTimingSnapshot(
            Interlocked.Read(ref eventCount),
            average,
            p95,
            Interlocked.Read(ref maximumTicks) * 1000d / Stopwatch.Frequency,
            Interlocked.Read(ref longGapCount));
    }

    private static void UpdateMaximum(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}

internal sealed class BoundedFrameDistribution
{
    private readonly long[] counts;

    internal BoundedFrameDistribution(int maximumExactFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExactFrameCount);
        counts = new long[checked(maximumExactFrameCount + 2)];
    }

    internal void Record(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        Interlocked.Increment(ref counts[Math.Min(frames, counts.Length - 1)]);
    }

    internal IReadOnlyDictionary<int, long> Snapshot()
    {
        var result = new SortedDictionary<int, long>();
        for (var index = 0; index < counts.Length; index++)
        {
            var count = Interlocked.Read(ref counts[index]);
            if (count > 0) result[index] = count;
        }
        return result;
    }
}

internal abstract class AudioPacketWorker : IDisposable
{
    private readonly Thread thread;
    private readonly SafeFileHandle sharedStopEvent;
    private readonly ManualResetEvent ready = new(false);
    private readonly ManualResetEvent completed = new(false);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int started;
    private int handlesDisposed;
    private int mmcssRegistered;

    protected AudioPacketWorker(string name, SafeFileHandle sharedStopEvent)
    {
        this.sharedStopEvent = sharedStopEvent;
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal Exception? Failure { get; private set; }
    internal bool MmcssRegistered => Volatile.Read(ref mmcssRegistered) != 0;
    internal WaitHandle CompletionWaitHandle => completed;
    internal Task Completion => completion.Task;
    internal bool HandlesReleased => Volatile.Read(ref handlesDisposed) != 0;

    internal bool StartAndWaitReady(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("An audio packet worker may start only once.");
        thread.Start();
        return ready.WaitOne(timeout) && Failure is null;
    }

    public void Dispose()
    {
        if (!completion.Task.IsCompleted) return;
        if (Interlocked.Exchange(ref handlesDisposed, 1) != 0) return;
        ready.Dispose();
        completed.Dispose();
    }

    protected abstract void RunPacketLoop();

    private unsafe void ThreadMain()
    {
        AvRevertMmThreadCharacteristicsSafeHandle? mmcss = null;
        var comInitialized = false;
        try
        {
            var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartment.Failed) Marshal.ThrowExceptionForHR(apartment.Value);
            comInitialized = true;
            mmcss = TryRegisterMmcss();
            Volatile.Write(ref mmcssRegistered, mmcss is null ? 0 : 1);
            ready.Set();
            RunPacketLoop();
        }
        catch (Exception exception)
        {
            Failure = exception;
            try { _ = PInvoke.SetEvent(sharedStopEvent); }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            try { ready.Set(); }
            catch (ObjectDisposedException) { }
            try { mmcss?.Dispose(); }
            finally
            {
                if (comInitialized) PInvoke.CoUninitialize();
                completed.Set();
                completion.TrySetResult();
            }
        }
    }

    private static AvRevertMmThreadCharacteristicsSafeHandle? TryRegisterMmcss()
    {
        try
        {
            uint taskIndex = 0;
            var handle = PInvoke.AvSetMmThreadCharacteristics("Audio", ref taskIndex);
            if (!handle.IsInvalid) _ = PInvoke.AvSetMmThreadPriority(handle, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);
            return handle.IsInvalid ? null : handle;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}

internal sealed class AudioCapturePacketWorker : AudioPacketWorker
{
    private const uint WaitMilliseconds = 1000;
    private readonly SafeFileHandle stopEvent;
    private readonly SafeFileHandle captureEvent;
    private readonly IAudioCaptureBufferAccess access;
    private readonly SpscAudioFrameBuffer buffer;
    private readonly AudioStreamFormat format;
    private readonly ManualResetEvent targetReached;
    private readonly int targetFrames;
    private readonly AudioEventTimingStatistics timing;
    private readonly BoundedFrameDistribution packetSizes;
    private readonly AudioDiscontinuityTracker discontinuities;
    private readonly Func<(double? Requested, double? Applied)> adjustmentSnapshot;
    private readonly Action<AudioCapturePacket, int, int> discontinuityObserver;
    private readonly Action<int> packetSizeObserver;
    private long emptyWakeCount;
    private long framesCaptured;
    private long silentFrames;
    private long discontinuityCount;
    private long timestampErrorCount;
    private long lastDevicePosition;
    private long lastQpcPosition;
    private long lastPacketUtcTicks;
    private int phase = (int)AudioDiscontinuityPhase.Startup;
    private int prerollComplete;

    internal AudioCapturePacketWorker(
        SafeFileHandle stopEvent,
        SafeFileHandle captureEvent,
        IAudioCaptureBufferAccess access,
        SpscAudioFrameBuffer buffer,
        AudioStreamFormat format,
        int targetFrames,
        int maximumPacketFrames,
        double expectedPeriodMilliseconds,
        AudioDiscontinuityTracker discontinuities,
        Func<(double? Requested, double? Applied)> adjustmentSnapshot)
        : base("Audio capture packet worker", stopEvent)
    {
        this.stopEvent = stopEvent;
        this.captureEvent = captureEvent;
        this.access = access;
        this.buffer = buffer;
        this.format = format;
        this.targetFrames = targetFrames;
        targetReached = new ManualResetEvent(false);
        timing = new AudioEventTimingStatistics(expectedPeriodMilliseconds * 2.5);
        packetSizes = new BoundedFrameDistribution(Math.Max(maximumPacketFrames, 1));
        this.discontinuities = discontinuities;
        this.adjustmentSnapshot = adjustmentSnapshot;
        discontinuityObserver = ObserveDiscontinuity;
        packetSizeObserver = packetSizes.Record;
    }

    internal WaitHandle TargetReached => targetReached;
    internal void CompletePreroll() => Volatile.Write(ref prerollComplete, 1);
    internal void SetPhase(AudioDiscontinuityPhase value) => Volatile.Write(ref phase, (int)value);

    internal AudioCaptureWorkerSnapshot Snapshot() => new(
        timing.Snapshot(),
        packetSizes.Snapshot(),
        Interlocked.Read(ref emptyWakeCount),
        Interlocked.Read(ref framesCaptured),
        Interlocked.Read(ref silentFrames),
        Interlocked.Read(ref discontinuityCount),
        Interlocked.Read(ref timestampErrorCount),
        unchecked((ulong)Interlocked.Read(ref lastDevicePosition)),
        unchecked((ulong)Interlocked.Read(ref lastQpcPosition)),
        ReadTimestamp(ref lastPacketUtcTicks),
        MmcssRegistered);

    public new void Dispose()
    {
        base.Dispose();
        if (HandlesReleased) targetReached.Dispose();
    }

    protected override void RunPacketLoop()
    {
        var waits = new[] { ToHandle(stopEvent), ToHandle(captureEvent) };
        while (true)
        {
            var wait = PInvoke.WaitForMultipleObjects(waits, false, WaitMilliseconds);
            if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
            if (wait == WAIT_EVENT.WAIT_FAILED)
                throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio capture event waiting failed.");
            if (wait != (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1)) continue;
            if (targetReached.WaitOne(0) && Volatile.Read(ref prerollComplete) == 0) continue;

            timing.Record(Stopwatch.GetTimestamp());
            var before = Interlocked.Read(ref framesCaptured);
            var result = AudioNativeBufferProcessor.ProcessCapture(
                access,
                buffer,
                format,
                discontinuityObserver,
                packetSizeObserver);
            Interlocked.Add(ref framesCaptured, result.FramesCaptured);
            Interlocked.Add(ref silentFrames, result.SilentFrames);
            Interlocked.Add(ref discontinuityCount, result.Discontinuities);
            Interlocked.Add(ref timestampErrorCount, result.TimestampErrors);
            if (result.HasPacket)
            {
                Interlocked.Exchange(ref lastDevicePosition, unchecked((long)result.LastDevicePosition));
                Interlocked.Exchange(ref lastQpcPosition, unchecked((long)result.LastQpcPosition));
                Interlocked.Exchange(ref lastPacketUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
            if (Interlocked.Read(ref framesCaptured) == before) Interlocked.Increment(ref emptyWakeCount);
            if (buffer.QueuedFrames >= targetFrames) targetReached.Set();
        }
    }

    private void ObserveDiscontinuity(AudioCapturePacket packet, int queueBefore, int queueAfter)
    {
        var adjustment = adjustmentSnapshot();
        var ring = buffer.Snapshot();
        discontinuities.Observe(
            Stopwatch.GetElapsedTime(0), packet, queueBefore, queueAfter,
            adjustment.Requested, adjustment.Applied,
            ring.StarvationEvents, ring.PhysicalCapacityEvents,
            (AudioDiscontinuityPhase)Volatile.Read(ref phase));
    }

    private static HANDLE ToHandle(SafeFileHandle value) => new(value.DangerousGetHandle());
    private static DateTimeOffset? ReadTimestamp(ref long location)
    {
        var ticks = Interlocked.Read(ref location);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}

internal sealed class AudioRenderPacketWorker : AudioPacketWorker
{
    private const uint WaitMilliseconds = 1000;
    private readonly SafeFileHandle stopEvent;
    private readonly SafeFileHandle renderEvent;
    private readonly IAudioRenderBufferAccess access;
    private readonly SpscAudioFrameBuffer buffer;
    private readonly AudioStreamFormat format;
    private readonly IAudioGainProcessor gain;
    private readonly uint renderBufferFrames;
    private readonly int renderPeriodFrames;
    private readonly AudioEventTimingStatistics timing;
    private readonly BoundedFrameDistribution availableFrames;
    private readonly AudioDiscontinuityTracker discontinuities;
    private long framesAvailableTotal;
    private long wakeCount;
    private int maximumFramesAvailable;
    private long lateWakeCount;
    private long nativeUnderfillEvents;
    private long framesRequested;
    private long liveFramesRendered;
    private long silenceInsertedFrames;
    private long lastRenderUtcTicks;
    private int currentPadding;

    internal AudioRenderPacketWorker(
        SafeFileHandle stopEvent,
        SafeFileHandle renderEvent,
        IAudioRenderBufferAccess access,
        SpscAudioFrameBuffer buffer,
        AudioStreamFormat format,
        IAudioGainProcessor gain,
        uint renderBufferFrames,
        int renderPeriodFrames,
        double expectedPeriodMilliseconds,
        AudioDiscontinuityTracker discontinuities)
        : base("Audio render packet worker", stopEvent)
    {
        this.stopEvent = stopEvent;
        this.renderEvent = renderEvent;
        this.access = access;
        this.buffer = buffer;
        this.format = format;
        this.gain = gain;
        this.renderBufferFrames = renderBufferFrames;
        this.renderPeriodFrames = renderPeriodFrames;
        timing = new AudioEventTimingStatistics(expectedPeriodMilliseconds * 2.5);
        availableFrames = new BoundedFrameDistribution(checked((int)Math.Max(renderBufferFrames, 1)));
        this.discontinuities = discontinuities;
    }

    internal AudioRenderWorkerSnapshot Snapshot()
    {
        var wakes = Interlocked.Read(ref wakeCount);
        return new AudioRenderWorkerSnapshot(
            timing.Snapshot(),
            availableFrames.Snapshot(),
            unchecked((uint)Volatile.Read(ref currentPadding)),
            wakes == 0 ? 0 : Interlocked.Read(ref framesAvailableTotal) / (double)wakes,
            Volatile.Read(ref maximumFramesAvailable),
            Interlocked.Read(ref lateWakeCount),
            Interlocked.Read(ref nativeUnderfillEvents),
            Interlocked.Read(ref framesRequested),
            Interlocked.Read(ref liveFramesRendered),
            Interlocked.Read(ref silenceInsertedFrames),
            ReadTimestamp(ref lastRenderUtcTicks),
            MmcssRegistered);
    }

    protected override void RunPacketLoop()
    {
        var waits = new[] { ToHandle(stopEvent), ToHandle(renderEvent) };
        while (true)
        {
            var wait = PInvoke.WaitForMultipleObjects(waits, false, WaitMilliseconds);
            if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
            if (wait == WAIT_EVENT.WAIT_FAILED)
                throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio render event waiting failed.");
            if (wait != (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1)) continue;

            timing.Record(Stopwatch.GetTimestamp());
            var result = AudioNativeBufferProcessor.ProcessRender(
                access, buffer, format, gain, renderBufferFrames);
            if (!result.Rendered) continue;
            var requested = result.Frames;
            Interlocked.Increment(ref wakeCount);
            Interlocked.Add(ref framesAvailableTotal, requested);
            UpdateMaximum(ref maximumFramesAvailable, requested);
            availableFrames.Record(requested);
            Volatile.Write(ref currentPadding, checked((int)result.PaddingFrames));
            if (requested > renderPeriodFrames) Interlocked.Increment(ref lateWakeCount);
            if (result.SilentFrames > 0) Interlocked.Increment(ref nativeUnderfillEvents);
            Interlocked.Add(ref framesRequested, requested);
            Interlocked.Add(ref liveFramesRendered, result.LiveFrames);
            Interlocked.Add(ref silenceInsertedFrames, result.SilentFrames);
            Interlocked.Exchange(ref lastRenderUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            var ring = buffer.Snapshot();
            discontinuities.UpdateOutcomes(
                Stopwatch.GetElapsedTime(0), ring.StarvationEvents, ring.PhysicalCapacityEvents);
        }
    }

    private static void UpdateMaximum(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static HANDLE ToHandle(SafeFileHandle value) => new(value.DangerousGetHandle());
    private static DateTimeOffset? ReadTimestamp(ref long location)
    {
        var ticks = Interlocked.Read(ref location);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}

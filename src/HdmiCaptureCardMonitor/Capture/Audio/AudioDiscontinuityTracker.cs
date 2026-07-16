namespace HdmiCaptureCardMonitor.Capture.Audio;

/// <summary>
/// Fixed-capacity, non-blocking discontinuity timeline. Capture is the sole
/// entry producer; either packet worker may atomically mark a proximate outcome.
/// </summary>
internal sealed class AudioDiscontinuityTracker
{
    private const int MaximumObservations = 256;
    internal static readonly TimeSpan DefaultOutcomeWindow = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan outcomeWindow;
    private readonly Entry?[] entries = new Entry[MaximumObservations];
    private long lastDevicePosition;
    private long lastQpcPosition;
    private int hasPosition;
    private int count;

    internal AudioDiscontinuityTracker(TimeSpan? outcomeWindow = null)
    {
        this.outcomeWindow = outcomeWindow ?? DefaultOutcomeWindow;
        ArgumentOutOfRangeException.ThrowIfLessThan(this.outcomeWindow, TimeSpan.Zero);
    }

    internal void Observe(
        TimeSpan monotonicTime,
        AudioCapturePacket packet,
        int queueBeforeFrames,
        int queueAfterFrames,
        double? requestedAdjustmentPpm,
        double? appliedAdjustmentPpm,
        long underrunCount,
        long overrunCount,
        AudioDiscontinuityPhase phase)
    {
        var index = Volatile.Read(ref count);
        if (index < MaximumObservations)
        {
            var hasPrevious = Volatile.Read(ref hasPosition) != 0;
            var previousDevice = unchecked((ulong)Interlocked.Read(ref lastDevicePosition));
            var previousQpc = unchecked((ulong)Interlocked.Read(ref lastQpcPosition));
            entries[index] = new Entry(new AudioDiscontinuityObservation(
                monotonicTime,
                checked((int)packet.FrameCount),
                PositionDelta(packet.DevicePosition, previousDevice, hasPrevious),
                PositionDelta(packet.QpcPosition, previousQpc, hasPrevious),
                queueBeforeFrames,
                queueAfterFrames,
                requestedAdjustmentPpm,
                appliedAdjustmentPpm,
                underrunCount,
                overrunCount,
                phase));
            Volatile.Write(ref count, index + 1);
        }

        Interlocked.Exchange(ref lastDevicePosition, unchecked((long)packet.DevicePosition));
        Interlocked.Exchange(ref lastQpcPosition, unchecked((long)packet.QpcPosition));
        Volatile.Write(ref hasPosition, 1);
    }

    internal void UpdateOutcomes(TimeSpan monotonicTime, long underrunCount, long overrunCount)
    {
        var published = Volatile.Read(ref count);
        for (var index = published - 1; index >= 0; index--)
        {
            var entry = Volatile.Read(ref entries[index]);
            if (entry is null) continue;
            var elapsed = monotonicTime - entry.Observation.MonotonicTime;
            if (elapsed < TimeSpan.Zero) continue;
            if (elapsed > outcomeWindow) break;
            if (underrunCount > entry.Observation.UnderrunCountAtObservation)
                Volatile.Write(ref entry.UnderrunFollowed, 1);
            if (overrunCount > entry.Observation.OverrunCountAtObservation)
                Volatile.Write(ref entry.OverrunFollowed, 1);
        }
    }

    internal IReadOnlyList<AudioDiscontinuityObservation> Snapshot()
    {
        var published = Volatile.Read(ref count);
        var result = new AudioDiscontinuityObservation[published];
        for (var index = 0; index < published; index++)
        {
            var entry = Volatile.Read(ref entries[index])!;
            result[index] = entry.Observation with
            {
                UnderrunFollowed = Volatile.Read(ref entry.UnderrunFollowed) != 0,
                OverrunFollowed = Volatile.Read(ref entry.OverrunFollowed) != 0
            };
        }
        return result;
    }

    private static long? PositionDelta(ulong current, ulong previous, bool hasPrevious)
    {
        if (!hasPrevious || current < previous) return null;
        var delta = current - previous;
        return delta <= long.MaxValue ? (long)delta : null;
    }

    private sealed class Entry(AudioDiscontinuityObservation observation)
    {
        internal AudioDiscontinuityObservation Observation { get; } = observation;
        internal int UnderrunFollowed;
        internal int OverrunFollowed;
    }
}

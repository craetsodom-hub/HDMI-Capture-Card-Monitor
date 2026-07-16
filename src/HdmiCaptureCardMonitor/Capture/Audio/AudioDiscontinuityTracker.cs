namespace HdmiCaptureCardMonitor.Capture.Audio;

/// <summary>
/// Retains a bounded packet-discontinuity timeline. Outcome correlation is
/// deliberately limited to a short window so an unrelated error much later in
/// the session is not attributed to an earlier discontinuity.
/// </summary>
internal sealed class AudioDiscontinuityTracker
{
    private const int MaximumObservations = 256;
    internal static readonly TimeSpan DefaultOutcomeWindow = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan outcomeWindow;
    private readonly List<AudioDiscontinuityObservation> observations = [];
    private ulong lastDevicePosition;
    private ulong lastQpcPosition;
    private bool hasPosition;

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
        if (observations.Count < MaximumObservations)
        {
            observations.Add(new AudioDiscontinuityObservation(
                monotonicTime,
                checked((int)packet.FrameCount),
                PositionDelta(packet.DevicePosition, lastDevicePosition, hasPosition),
                PositionDelta(packet.QpcPosition, lastQpcPosition, hasPosition),
                queueBeforeFrames,
                queueAfterFrames,
                requestedAdjustmentPpm,
                appliedAdjustmentPpm,
                underrunCount,
                overrunCount,
                phase));
        }

        lastDevicePosition = packet.DevicePosition;
        lastQpcPosition = packet.QpcPosition;
        hasPosition = true;
    }

    internal void UpdateOutcomes(TimeSpan monotonicTime, long underrunCount, long overrunCount)
    {
        for (var index = observations.Count - 1; index >= 0; index--)
        {
            var observation = observations[index];
            var elapsed = monotonicTime - observation.MonotonicTime;
            if (elapsed < TimeSpan.Zero) continue;
            if (elapsed > outcomeWindow) break;
            observations[index] = observation with
            {
                UnderrunFollowed = observation.UnderrunFollowed ||
                    underrunCount > observation.UnderrunCountAtObservation,
                OverrunFollowed = observation.OverrunFollowed ||
                    overrunCount > observation.OverrunCountAtObservation
            };
        }
    }

    internal IReadOnlyList<AudioDiscontinuityObservation> Snapshot() => observations.ToArray();

    private static long? PositionDelta(ulong current, ulong previous, bool hasPrevious)
    {
        if (!hasPrevious || current < previous) return null;
        var delta = current - previous;
        return delta <= long.MaxValue ? (long)delta : null;
    }
}

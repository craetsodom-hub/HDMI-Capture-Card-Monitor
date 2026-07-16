namespace HdmiCaptureCardMonitor.Capture.Audio;

internal readonly record struct AudioQueueRateControllerSample(
    int QueueFrames,
    long CaptureFrames,
    long RenderFrames,
    long DiscontinuityCount,
    long LateRenderWakeCount);

internal readonly record struct AudioQueueRateControllerDecision(
    double FilteredQueueFrames,
    double RequestedAdjustmentPpm,
    double AppliedAdjustmentPpm,
    TimeSpan SaturationDuration,
    long DirectionChangeCount,
    bool IsSaturated,
    double? EstimatedClockDriftPpm,
    bool IsAdjustmentActive,
    TimeSpan ActivationDuration);

internal sealed class AudioQueueRateControllerPolicy
{
    internal const double DefaultMaximumAdjustmentPpm = 1_000;
    internal const double DefaultDeadbandPeriods = 0.25;
    internal const double DefaultSlewPpmPerSecond = 400;
    internal const int InitialObservationIntervals = 20;
    internal const int SustainedEvidenceIntervals = 6;
    internal const int TransientFreezeIntervals = 4;
    private const double QueueFilterWeight = 0.20;
    private const double DriftFilterWeight = 0.15;
    private const double DriftDeadbandPpm = 30;
    private const double ZeroTolerance = 0.0001;

    private readonly int targetQueueFrames;
    private readonly int periodFrames;
    private readonly double queueDeadbandFrames;
    private readonly double maximumAdjustmentPpm;
    private readonly double slewPpmPerSecond;
    private double filteredQueueFrames;
    private double filteredDriftPpm;
    private double appliedAdjustmentPpm;
    private TimeSpan saturationDuration;
    private TimeSpan activationDuration;
    private long previousCaptureFrames;
    private long previousRenderFrames;
    private long previousDiscontinuities;
    private long previousLateWakes;
    private int observationIntervals;
    private int evidenceIntervals;
    private int freezeIntervals;
    private int lastEvidenceDirection;
    private int lastNonZeroDirection;
    private long directionChangeCount;
    private bool hasPrevious;
    private bool active;

    internal AudioQueueRateControllerPolicy(
        int targetQueueFrames,
        int periodFrames,
        double maximumAdjustmentPpm = DefaultMaximumAdjustmentPpm,
        double deadbandPeriods = DefaultDeadbandPeriods,
        double slewPpmPerSecond = DefaultSlewPpmPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetQueueFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAdjustmentPpm);
        ArgumentOutOfRangeException.ThrowIfNegative(deadbandPeriods);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slewPpmPerSecond);
        this.targetQueueFrames = targetQueueFrames;
        this.periodFrames = periodFrames;
        this.maximumAdjustmentPpm = maximumAdjustmentPpm;
        this.slewPpmPerSecond = slewPpmPerSecond;
        queueDeadbandFrames = periodFrames * deadbandPeriods;
        filteredQueueFrames = targetQueueFrames;
    }

    internal AudioQueueRateControllerDecision Step(AudioQueueRateControllerSample sample, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sample.QueueFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(sample.CaptureFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(sample.RenderFrames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        filteredQueueFrames += (sample.QueueFrames - filteredQueueFrames) * QueueFilterWeight;

        double? driftEstimate = null;
        var transient = false;
        if (hasPrevious)
        {
            var captureDelta = sample.CaptureFrames - previousCaptureFrames;
            var renderDelta = sample.RenderFrames - previousRenderFrames;
            transient = sample.DiscontinuityCount != previousDiscontinuities ||
                sample.LateRenderWakeCount != previousLateWakes;
            if (!transient && captureDelta >= 0 && renderDelta > 0)
            {
                var rawDrift = (captureDelta - renderDelta) * 1_000_000d / renderDelta;
                filteredDriftPpm += (rawDrift - filteredDriftPpm) * DriftFilterWeight;
                driftEstimate = filteredDriftPpm;
                observationIntervals++;
            }
        }
        else
        {
            hasPrevious = true;
        }

        previousCaptureFrames = sample.CaptureFrames;
        previousRenderFrames = sample.RenderFrames;
        previousDiscontinuities = sample.DiscontinuityCount;
        previousLateWakes = sample.LateRenderWakeCount;

        if (transient)
        {
            freezeIntervals = TransientFreezeIntervals;
            evidenceIntervals = 0;
            lastEvidenceDirection = 0;
        }
        else if (freezeIntervals > 0)
        {
            freezeIntervals--;
        }

        var queueError = filteredQueueFrames - targetQueueFrames;
        var driftDirection = Math.Sign(filteredDriftPpm);
        var queueDirection = Math.Abs(queueError) <= queueDeadbandFrames ? 0 : Math.Sign(queueError);
        var evidenceDirection = observationIntervals >= InitialObservationIntervals &&
            freezeIntervals == 0 && Math.Abs(filteredDriftPpm) > DriftDeadbandPpm &&
            driftDirection == queueDirection
                ? driftDirection
                : 0;
        if (evidenceDirection == 0)
        {
            evidenceIntervals = 0;
            lastEvidenceDirection = 0;
        }
        else if (evidenceDirection == lastEvidenceDirection)
        {
            evidenceIntervals++;
        }
        else
        {
            evidenceIntervals = 1;
            lastEvidenceDirection = evidenceDirection;
        }

        if (evidenceIntervals >= SustainedEvidenceIntervals) active = true;
        if (active && Math.Abs(filteredDriftPpm) <= DriftDeadbandPpm && queueDirection == 0) active = false;
        if (freezeIntervals > 0) active = false;

        var requestedAdjustmentPpm = active
            ? Math.Clamp(filteredDriftPpm, -maximumAdjustmentPpm, maximumAdjustmentPpm)
            : 0;
        var requestedDirection = Math.Sign(requestedAdjustmentPpm);
        var appliedDirection = Math.Sign(appliedAdjustmentPpm);
        var slewTarget = appliedDirection != 0 && requestedDirection != 0 && appliedDirection != requestedDirection
            ? 0
            : requestedAdjustmentPpm;
        var maximumStep = slewPpmPerSecond * interval.TotalSeconds;
        appliedAdjustmentPpm = MoveTowards(appliedAdjustmentPpm, slewTarget, maximumStep);
        if (Math.Abs(appliedAdjustmentPpm) < ZeroTolerance) appliedAdjustmentPpm = 0;

        var newDirection = Math.Sign(appliedAdjustmentPpm);
        if (newDirection != 0 && lastNonZeroDirection != 0 && newDirection != lastNonZeroDirection)
            directionChangeCount++;
        if (newDirection != 0) lastNonZeroDirection = newDirection;

        var saturated = Math.Abs(appliedAdjustmentPpm) >= maximumAdjustmentPpm - ZeroTolerance;
        if (saturated) saturationDuration += interval;
        if (active) activationDuration += interval;
        return new AudioQueueRateControllerDecision(
            filteredQueueFrames,
            requestedAdjustmentPpm,
            appliedAdjustmentPpm,
            saturationDuration,
            directionChangeCount,
            saturated,
            driftEstimate,
            active,
            activationDuration);
    }

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        var delta = target - current;
        if (Math.Abs(delta) <= maximumDelta) return target;
        return current + Math.CopySign(maximumDelta, delta);
    }
}

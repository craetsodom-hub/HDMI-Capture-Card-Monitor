namespace HdmiCaptureCardMonitor.Capture.Audio;

internal readonly record struct AudioQueueRateControllerDecision(
    double FilteredQueueFrames,
    double RequestedAdjustmentPpm,
    double AppliedAdjustmentPpm,
    TimeSpan SaturationDuration,
    long DirectionChangeCount,
    bool IsSaturated);

internal sealed class AudioQueueRateControllerPolicy
{
    internal const double DefaultMaximumAdjustmentPpm = 1_000;
    internal const double DefaultDeadbandPeriods = 0.25;
    internal const double DefaultSlewPpmPerSecond = 400;
    private const double FilterWeight = 0.20;
    private const double ProportionalPpmPerPeriod = 500;
    private const double ZeroTolerance = 0.0001;

    private readonly int targetQueueFrames;
    private readonly int periodFrames;
    private readonly double deadbandFrames;
    private readonly double maximumAdjustmentPpm;
    private readonly double slewPpmPerSecond;
    private double filteredQueueFrames;
    private double appliedAdjustmentPpm;
    private TimeSpan saturationDuration;
    private int lastNonZeroDirection;
    private long directionChangeCount;

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
        deadbandFrames = periodFrames * deadbandPeriods;
        filteredQueueFrames = targetQueueFrames;
    }

    internal AudioQueueRateControllerDecision Step(int queueFrames, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queueFrames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        filteredQueueFrames += (queueFrames - filteredQueueFrames) * FilterWeight;
        var error = filteredQueueFrames - targetQueueFrames;
        double requestedAdjustmentPpm;
        if (Math.Abs(error) <= deadbandFrames)
        {
            requestedAdjustmentPpm = 0;
        }
        else
        {
            var correctedError = error - Math.CopySign(deadbandFrames, error);
            requestedAdjustmentPpm = Math.Clamp(
                correctedError / periodFrames * ProportionalPpmPerPeriod,
                -maximumAdjustmentPpm,
                maximumAdjustmentPpm);
        }

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
        return new AudioQueueRateControllerDecision(
            filteredQueueFrames,
            requestedAdjustmentPpm,
            appliedAdjustmentPpm,
            saturationDuration,
            directionChangeCount,
            saturated);
    }

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        var delta = target - current;
        if (Math.Abs(delta) <= maximumDelta) return target;
        return current + Math.CopySign(maximumDelta, delta);
    }
}

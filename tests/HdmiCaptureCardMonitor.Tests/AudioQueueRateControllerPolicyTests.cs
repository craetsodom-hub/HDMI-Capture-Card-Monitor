using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioQueueRateControllerPolicyTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void StableTargetQueueKeepsNominalRate()
    {
        var policy = CreatePolicy();
        var decisions = Enumerable.Range(0, 20).Select(_ => policy.Step(960, Interval)).ToArray();
        Assert.All(decisions, decision =>
        {
            Assert.Equal(0, decision.RequestedAdjustmentPpm);
            Assert.Equal(0, decision.AppliedAdjustmentPpm);
        });
    }

    [Fact]
    public void SmallNoiseInsideDeadbandDoesNotMoveRate()
    {
        var policy = CreatePolicy();
        var queues = new[] { 900, 1020, 870, 1050, 960, 1000, 920 };
        Assert.All(queues.Select(queue => policy.Step(queue, Interval)), decision =>
            Assert.Equal(0, decision.AppliedAdjustmentPpm));
    }

    [Fact]
    public void SustainedPositiveDriftRequestsAndAppliesBoundedPositiveCorrection()
    {
        var final = Run(CreatePolicy(), 2400, 30)[^1];
        Assert.InRange(final.RequestedAdjustmentPpm, 1, 1000);
        Assert.InRange(final.AppliedAdjustmentPpm, 1, 1000);
    }

    [Fact]
    public void SustainedNegativeDriftRequestsAndAppliesBoundedNegativeCorrection()
    {
        var final = Run(CreatePolicy(), 0, 30)[^1];
        Assert.InRange(final.RequestedAdjustmentPpm, -1000, -1);
        Assert.InRange(final.AppliedAdjustmentPpm, -1000, -1);
    }

    [Fact]
    public void SuddenSchedulingDisturbanceIsFilteredAndSlewLimited()
    {
        var policy = CreatePolicy();
        _ = Run(policy, 960, 5);
        var decision = policy.Step(2880, Interval);
        Assert.InRange(decision.FilteredQueueFrames, 961, 2879);
        Assert.InRange(Math.Abs(decision.AppliedAdjustmentPpm), 0, 200);
    }

    [Fact]
    public void DeadbandRemovesCorrectionNearTarget()
    {
        var policy = CreatePolicy();
        var decisions = Run(policy, 1000, 30);
        Assert.Equal(0, decisions[^1].RequestedAdjustmentPpm);
        Assert.Equal(0, decisions[^1].AppliedAdjustmentPpm);
    }

    [Fact]
    public void SlewLimitCapsEachAppliedChange()
    {
        var decisions = Run(CreatePolicy(), 2880, 20);
        for (var index = 1; index < decisions.Length; index++)
            Assert.InRange(Math.Abs(decisions[index].AppliedAdjustmentPpm - decisions[index - 1].AppliedAdjustmentPpm), 0, 200);
    }

    [Fact]
    public void AppliedCorrectionDoesNotJumpAcrossPolarity()
    {
        var policy = CreatePolicy();
        var decisions = Run(policy, 2880, 20).Concat(Run(policy, 0, 40)).ToArray();
        for (var index = 1; index < decisions.Length; index++)
            Assert.True(decisions[index - 1].AppliedAdjustmentPpm * decisions[index].AppliedAdjustmentPpm >= 0);
        Assert.InRange(decisions[^1].DirectionChangeCount, 0, 1);
    }

    [Fact]
    public void CorrectionAndSaturationRemainBounded()
    {
        var decisions = Run(CreatePolicy(), 10000, 40);
        Assert.All(decisions, decision =>
        {
            Assert.InRange(decision.RequestedAdjustmentPpm, -1000, 1000);
            Assert.InRange(decision.AppliedAdjustmentPpm, -1000, 1000);
        });
        Assert.True(decisions[^1].SaturationDuration > TimeSpan.Zero);
    }

    [Fact]
    public void StableQueueReturnsAppliedRateTowardNominal()
    {
        var policy = CreatePolicy();
        _ = Run(policy, 2880, 20);
        var recovery = Run(policy, 960, 50);
        Assert.Equal(0, recovery[^1].RequestedAdjustmentPpm);
        Assert.Equal(0, recovery[^1].AppliedAdjustmentPpm);
    }

    [Fact]
    public void PolicyRejectsInvalidMeasurements()
    {
        var policy = CreatePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Step(-1, Interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Step(960, TimeSpan.Zero));
    }

    private static AudioQueueRateControllerPolicy CreatePolicy() => new(960, 480);

    private static AudioQueueRateControllerDecision[] Run(
        AudioQueueRateControllerPolicy policy,
        int queueFrames,
        int count) =>
        Enumerable.Range(0, count).Select(_ => policy.Step(queueFrames, Interval)).ToArray();
}

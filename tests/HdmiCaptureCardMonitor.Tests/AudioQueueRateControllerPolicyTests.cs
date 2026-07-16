using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioQueueRateControllerPolicyTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void StableEqualClocksRemainNominalAfterObservationWindow()
    {
        var decisions = Run(CreatePolicy(), 50, 960, 24_000, 24_000);
        Assert.All(decisions, decision =>
        {
            Assert.Equal(0, decision.RequestedAdjustmentPpm);
            Assert.Equal(0, decision.AppliedAdjustmentPpm);
            Assert.False(decision.IsAdjustmentActive);
        });
    }

    [Fact]
    public void SmallQueueNoiseDoesNotActivateAdjustment()
    {
        var policy = CreatePolicy();
        long capture = 0;
        long render = 0;
        var queues = new[] { 900, 1020, 870, 1050, 960, 1000, 920 };
        for (var index = 0; index < 70; index++)
        {
            capture += 24_000;
            render += 24_000;
            var decision = policy.Step(new AudioQueueRateControllerSample(
                queues[index % queues.Length], capture, render, 0, 0), Interval);
            Assert.Equal(0, decision.AppliedAdjustmentPpm);
        }
    }

    [Fact]
    public void GenuineSustainedPositiveDriftActivatesGradualBoundedCorrection()
    {
        var decisions = Run(CreatePolicy(), 60, 1_440, 24_024, 24_000);
        Assert.Contains(decisions, decision => decision.IsAdjustmentActive);
        Assert.InRange(decisions[^1].RequestedAdjustmentPpm, 1, 1000);
        Assert.InRange(decisions[^1].AppliedAdjustmentPpm, 1, 1000);
        Assert.True(decisions[^1].ActivationDuration > TimeSpan.Zero);
    }

    [Fact]
    public void GenuineSustainedNegativeDriftActivatesGradualBoundedCorrection()
    {
        var decisions = Run(CreatePolicy(), 60, 480, 23_976, 24_000);
        Assert.Contains(decisions, decision => decision.IsAdjustmentActive);
        Assert.InRange(decisions[^1].RequestedAdjustmentPpm, -1000, -1);
        Assert.InRange(decisions[^1].AppliedAdjustmentPpm, -1000, -1);
    }

    [Fact]
    public void OneLateRenderWakeDoesNotBecomeClockDriftEvidence()
    {
        var policy = CreatePolicy();
        long capture = 0;
        long render = 0;
        long late = 0;
        var decisions = new List<AudioQueueRateControllerDecision>();
        for (var index = 0; index < 50; index++)
        {
            capture += 24_000;
            render += index == 25 ? 20_000 : 24_000;
            if (index == 25) late++;
            decisions.Add(policy.Step(new AudioQueueRateControllerSample(
                index == 25 ? 1_440 : 960, capture, render, 0, late), Interval));
        }
        Assert.All(decisions, decision => Assert.Equal(0, decision.AppliedAdjustmentPpm));
    }

    [Fact]
    public void CaptureDiscontinuityTemporarilyFreezesActiveAdjustment()
    {
        var policy = CreatePolicy();
        var state = Advance(policy, 60, 1_440, 24_024, 24_000);
        Assert.True(state.Decision.IsAdjustmentActive);
        state.Capture += 24_024;
        state.Render += 24_000;
        var frozen = policy.Step(new AudioQueueRateControllerSample(
            1_440, state.Capture, state.Render, 1, 0), Interval);
        Assert.False(frozen.IsAdjustmentActive);
        Assert.Equal(0, frozen.RequestedAdjustmentPpm);
        Assert.True(Math.Abs(frozen.AppliedAdjustmentPpm) < Math.Abs(state.Decision.AppliedAdjustmentPpm));
    }

    [Fact]
    public void QueueRecoveryReturnsNominalWithoutOppositePolarity()
    {
        var policy = CreatePolicy();
        var state = Advance(policy, 60, 1_440, 24_024, 24_000);
        var decisions = new List<AudioQueueRateControllerDecision>();
        for (var index = 0; index < 160; index++)
        {
            state.Capture += 24_000;
            state.Render += 24_000;
            decisions.Add(policy.Step(new AudioQueueRateControllerSample(
                960, state.Capture, state.Render, 0, 0), Interval));
        }
        Assert.DoesNotContain(decisions, decision => decision.AppliedAdjustmentPpm < 0);
        Assert.Equal(0, decisions[^1].AppliedAdjustmentPpm);
    }

    [Fact]
    public void SlewAndMaximumCorrectionRemainBounded()
    {
        var decisions = Run(CreatePolicy(), 80, 2_400, 24_240, 24_000);
        for (var index = 1; index < decisions.Length; index++)
            Assert.InRange(Math.Abs(
                decisions[index].AppliedAdjustmentPpm - decisions[index - 1].AppliedAdjustmentPpm), 0, 200);
        Assert.All(decisions, decision =>
        {
            Assert.InRange(decision.RequestedAdjustmentPpm, -1000, 1000);
            Assert.InRange(decision.AppliedAdjustmentPpm, -1000, 1000);
        });
    }

    [Fact]
    public void PolicyRejectsInvalidMeasurements()
    {
        var policy = CreatePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Step(
            new AudioQueueRateControllerSample(-1, 0, 0, 0, 0), Interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Step(
            new AudioQueueRateControllerSample(960, 0, 0, 0, 0), TimeSpan.Zero));
    }

    private static AudioQueueRateControllerPolicy CreatePolicy() => new(960, 480);

    private static AudioQueueRateControllerDecision[] Run(
        AudioQueueRateControllerPolicy policy,
        int count,
        int queueFrames,
        long captureFramesPerInterval,
        long renderFramesPerInterval)
    {
        var result = new AudioQueueRateControllerDecision[count];
        long capture = 0;
        long render = 0;
        for (var index = 0; index < count; index++)
        {
            capture += captureFramesPerInterval;
            render += renderFramesPerInterval;
            result[index] = policy.Step(new AudioQueueRateControllerSample(
                queueFrames, capture, render, 0, 0), Interval);
        }
        return result;
    }

    private static (long Capture, long Render, AudioQueueRateControllerDecision Decision) Advance(
        AudioQueueRateControllerPolicy policy,
        int count,
        int queueFrames,
        long captureFramesPerInterval,
        long renderFramesPerInterval)
    {
        long capture = 0;
        long render = 0;
        AudioQueueRateControllerDecision decision = default;
        for (var index = 0; index < count; index++)
        {
            capture += captureFramesPerInterval;
            render += renderFramesPerInterval;
            decision = policy.Step(new AudioQueueRateControllerSample(
                queueFrames, capture, render, 0, 0), Interval);
        }
        return (capture, render, decision);
    }
}

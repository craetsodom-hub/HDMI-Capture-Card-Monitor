using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioDiscontinuityTrackerTests
{
    [Fact]
    public void RecordsPacketContextAndImmediateOutcome()
    {
        var tracker = new AudioDiscontinuityTracker();
        tracker.Observe(
            TimeSpan.FromSeconds(1),
            new AudioCapturePacket(0, 480, 0, 1000, 2000),
            960, 1440, -25, -20, 3, 4, AudioDiscontinuityPhase.SteadyState);

        tracker.UpdateOutcomes(TimeSpan.FromMilliseconds(1050), 4, 4);
        var observation = Assert.Single(tracker.Snapshot());

        Assert.Equal(480, observation.PacketFrameCount);
        Assert.Equal(960, observation.QueueBeforeFrames);
        Assert.Equal(1440, observation.QueueAfterFrames);
        Assert.True(observation.UnderrunFollowed);
        Assert.False(observation.OverrunFollowed);
    }

    [Fact]
    public void DoesNotAttributeUnrelatedLaterOutcome()
    {
        var tracker = new AudioDiscontinuityTracker();
        tracker.Observe(
            TimeSpan.FromSeconds(1),
            new AudioCapturePacket(0, 480, 0, 1000, 2000),
            960, 1440, 0, 0, 0, 0, AudioDiscontinuityPhase.SteadyState);

        tracker.UpdateOutcomes(TimeSpan.FromSeconds(2), 1, 1);
        var observation = Assert.Single(tracker.Snapshot());

        Assert.False(observation.UnderrunFollowed);
        Assert.False(observation.OverrunFollowed);
    }

    [Fact]
    public void RecordsDeltasBetweenConsecutiveDiscontinuities()
    {
        var tracker = new AudioDiscontinuityTracker();
        tracker.Observe(TimeSpan.Zero, new AudioCapturePacket(0, 96, 0, 1000, 2000),
            0, 96, 0, 0, 0, 0, AudioDiscontinuityPhase.Startup);
        tracker.Observe(TimeSpan.FromMilliseconds(10), new AudioCapturePacket(0, 480, 0, 1480, 102000),
            96, 576, 0, 0, 0, 0, AudioDiscontinuityPhase.Transition);

        var observations = tracker.Snapshot();
        Assert.Null(observations[0].DevicePositionDelta);
        Assert.Null(observations[0].QpcPositionDelta);
        Assert.Equal(480, observations[1].DevicePositionDelta);
        Assert.Equal(100000, observations[1].QpcPositionDelta);
    }
}

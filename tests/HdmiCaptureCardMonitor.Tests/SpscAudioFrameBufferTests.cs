using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class SpscAudioFrameBufferTests
{
    [Fact]
    public void ProducerConsumerWraparoundPreservesCompleteStereoFrames()
    {
        var buffer = new SpscAudioFrameBuffer(8, 2, 8);
        Assert.Equal(6, buffer.Write(CreateFrames(0, 6), 6).WrittenFrames);
        var first = new float[8];
        Assert.Equal(4, buffer.Read(first, 4).AudioFrames);
        Assert.Equal(6, buffer.Write(CreateFrames(6, 6), 6).WrittenFrames);
        var second = new float[16];
        Assert.Equal(8, buffer.Read(second, 8).AudioFrames);
        AssertStereoSequence(first, 0, 4);
        AssertStereoSequence(second, 4, 8);
    }

    [Fact]
    public void MillionConcurrentFramesHaveNoDuplicationReorderingOrTornStereo()
    {
        const int totalFrames = 1_000_000;
        var buffer = new SpscAudioFrameBuffer(4096, 2, 4096);
        Exception? producerFailure = null;
        Exception? consumerFailure = null;
        using var start = new ManualResetEvent(false);
        var producer = new Thread(() =>
        {
            try
            {
                var frame = new float[2];
                start.WaitOne();
                for (var index = 0; index < totalFrames; index++)
                {
                    var spinner = new SpinWait();
                    while (buffer.QueuedFrames >= buffer.CapacityFrames) spinner.SpinOnce();
                    frame[0] = index;
                    frame[1] = -index;
                    var write = buffer.Write(frame, 1);
                    Assert.Equal(1, write.WrittenFrames);
                    Assert.Equal(0, write.DroppedFrames);
                }
            }
            catch (Exception exception) { producerFailure = exception; }
        }) { IsBackground = true };
        var consumer = new Thread(() =>
        {
            try
            {
                var frame = new float[2];
                start.WaitOne();
                for (var expected = 0; expected < totalFrames; expected++)
                {
                    var spinner = new SpinWait();
                    while (buffer.QueuedFrames == 0) spinner.SpinOnce();
                    var read = buffer.Read(frame, 1);
                    Assert.Equal(1, read.AudioFrames);
                    Assert.Equal(expected, frame[0]);
                    Assert.Equal(-expected, frame[1]);
                }
            }
            catch (Exception exception) { consumerFailure = exception; }
        }) { IsBackground = true };
        producer.Start();
        consumer.Start();
        start.Set();
        Assert.True(producer.Join(TimeSpan.FromSeconds(30)));
        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(producerFailure);
        Assert.Null(consumerFailure);
        var snapshot = buffer.Snapshot();
        Assert.Equal(totalFrames, snapshot.ProducerSequence);
        Assert.Equal(totalFrames, snapshot.ConsumerSequence);
        Assert.Equal(0, snapshot.PhysicalCapacityDroppedFrames);
        Assert.Equal(0, snapshot.LatencyTrimmedFrames);
    }

    [Fact]
    public void ProducerFasterThanConsumerDiscardsIncomingFramesAtPhysicalCapacity()
    {
        var buffer = new SpscAudioFrameBuffer(8, 2, 8);
        var result = buffer.Write(CreateFrames(0, 12), 12);
        Assert.Equal(8, result.WrittenFrames);
        Assert.Equal(4, result.PhysicalCapacityDroppedFrames);
        var snapshot = buffer.Snapshot();
        Assert.Equal(1, snapshot.PhysicalCapacityEvents);
        Assert.Equal(4, snapshot.PhysicalCapacityDroppedFrames);
    }

    [Fact]
    public void ConsumerFasterThanProducerReturnsOnlyMissingTailAsSilence()
    {
        var buffer = new SpscAudioFrameBuffer(8, 2, 8);
        _ = buffer.Write(CreateFrames(0, 3), 3);
        var destination = Enumerable.Repeat(99f, 10).ToArray();
        var result = buffer.Read(destination, 5);
        Assert.Equal(3, result.AudioFrames);
        Assert.Equal(2, result.SilentFrames);
        Assert.All(destination[6..], sample => Assert.Equal(0, sample));
        Assert.Equal(1, buffer.Snapshot().StarvationEvents);
    }

    [Fact]
    public void ConsumerOwnsLatencyTrimmingAndPreservesChannelAlignment()
    {
        var buffer = new SpscAudioFrameBuffer(12, 2, 6);
        _ = buffer.Write(CreateFrames(0, 10), 10);
        var destination = new float[8];
        var result = buffer.Read(destination, 4);
        Assert.Equal(4, result.LatencyTrimmedFrames);
        AssertStereoSequence(destination, 4, 4);
        var snapshot = buffer.Snapshot();
        Assert.Equal(1, snapshot.LatencyTrimEvents);
        Assert.Equal(4, snapshot.LatencyTrimmedFrames);
    }

    [Fact]
    public void CheckedFrameArithmeticRejectsImpossibleSampleSizing()
    {
        Assert.Throws<OverflowException>(() => new SpscAudioFrameBuffer(int.MaxValue, int.MaxValue, 1));
        var buffer = new SpscAudioFrameBuffer(4, 2, 4);
        Assert.Throws<ArgumentException>(() => buffer.Write([1f], 1));
        Assert.Throws<ArgumentException>(() => buffer.Read(new float[1], 1));
    }

    [Fact]
    public void ConcurrentShutdownLeavesCoherentAtomicSnapshot()
    {
        var buffer = new SpscAudioFrameBuffer(256, 2, 256);
        using var stop = new ManualResetEvent(false);
        using var reached = new CountdownEvent(2);
        var producer = new Thread(() =>
        {
            var frame = new float[2];
            for (var index = 0; index < 20_000; index++)
            {
                if (buffer.QueuedFrames < buffer.CapacityFrames) _ = buffer.Write(frame, 1);
                if (index == 10_000) reached.Signal();
                if (stop.WaitOne(0)) break;
            }
        }) { IsBackground = true };
        var consumer = new Thread(() =>
        {
            var frame = new float[2];
            for (var index = 0; index < 20_000; index++)
            {
                if (buffer.QueuedFrames > 0) _ = buffer.Read(frame, 1);
                if (index == 10_000) reached.Signal();
                if (stop.WaitOne(0)) break;
            }
        }) { IsBackground = true };
        producer.Start();
        consumer.Start();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
        stop.Set();
        Assert.True(producer.Join(TimeSpan.FromSeconds(5)));
        Assert.True(consumer.Join(TimeSpan.FromSeconds(5)));
        var snapshot = buffer.Snapshot();
        Assert.InRange(snapshot.QueuedFrames, 0, buffer.CapacityFrames);
        Assert.True(snapshot.ProducerSequence >= snapshot.ConsumerSequence);
    }

    private static float[] CreateFrames(int first, int count)
    {
        var result = new float[count * 2];
        for (var index = 0; index < count; index++)
        {
            result[index * 2] = first + index;
            result[index * 2 + 1] = -(first + index);
        }
        return result;
    }

    private static void AssertStereoSequence(float[] samples, int first, int frames)
    {
        for (var index = 0; index < frames; index++)
        {
            Assert.Equal(first + index, samples[index * 2]);
            Assert.Equal(-(first + index), samples[index * 2 + 1]);
        }
    }
}

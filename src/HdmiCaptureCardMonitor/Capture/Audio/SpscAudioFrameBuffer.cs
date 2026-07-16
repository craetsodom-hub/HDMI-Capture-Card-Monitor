namespace HdmiCaptureCardMonitor.Capture.Audio;

internal readonly record struct SpscAudioBufferSnapshot(
    long ProducerSequence,
    long ConsumerSequence,
    int QueuedFrames,
    int MinimumQueuedFrames,
    int MaximumQueuedFrames,
    long QueueSampleTotal,
    long QueueSampleCount,
    long StarvationEvents,
    long SilentFrames,
    long PhysicalCapacityEvents,
    long PhysicalCapacityDroppedFrames,
    long LatencyTrimEvents,
    long LatencyTrimmedFrames)
{
    internal double AverageQueuedFrames => QueueSampleCount == 0
        ? 0
        : QueueSampleTotal / (double)QueueSampleCount;
}

/// <summary>
/// Preallocated lock-free single-producer/single-consumer interleaved-frame
/// buffer. Producer publication uses release ordering and consumer observation
/// uses acquire ordering through Volatile.Write/Read on monotonic sequences.
/// </summary>
internal sealed class SpscAudioFrameBuffer : IAudioFrameBuffer
{
    private readonly float[] samples;
    private readonly int channelCount;
    private readonly int highWatermarkFrames;
    private long producerSequence;
    private long consumerSequence;
    private int minimumQueuedFrames = int.MaxValue;
    private int maximumQueuedFrames;
    private long queueSampleTotal;
    private long queueSampleCount;
    private long starvationEvents;
    private long silentFrames;
    private long physicalCapacityEvents;
    private long physicalCapacityDroppedFrames;
    private long latencyTrimEvents;
    private long latencyTrimmedFrames;

    internal SpscAudioFrameBuffer(int capacityFrames, int channelCount, int highWatermarkFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(highWatermarkFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(highWatermarkFrames, capacityFrames);
        samples = new float[checked(capacityFrames * channelCount)];
        CapacityFrames = capacityFrames;
        this.channelCount = channelCount;
        this.highWatermarkFrames = highWatermarkFrames;
    }

    internal int CapacityFrames { get; }
    internal int ChannelCount => channelCount;
    internal int HighWatermarkFrames => highWatermarkFrames;
    public int QueuedFrames => Snapshot().QueuedFrames;

    public AudioBufferWriteResult Write(ReadOnlySpan<float> source, int frameCount, bool silent = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        var requestedSamples = checked(frameCount * channelCount);
        if (!silent && source.Length < requestedSamples)
            throw new ArgumentException("The sample span does not contain complete interleaved frames.", nameof(source));
        if (frameCount == 0) return default;

        var producer = producerSequence;
        var consumer = Volatile.Read(ref consumerSequence);
        var queued = ClampQueue(producer - consumer);
        var writable = CapacityFrames - queued;
        var written = Math.Min(frameCount, writable);
        if (written > 0)
        {
            CopyIntoRing(source, written, silent, producer);
            producer += written;
            Volatile.Write(ref producerSequence, producer);
        }

        var dropped = frameCount - written;
        if (dropped > 0)
        {
            Interlocked.Increment(ref physicalCapacityEvents);
            Interlocked.Add(ref physicalCapacityDroppedFrames, dropped);
        }
        RecordQueue(ClampQueue(producer - Volatile.Read(ref consumerSequence)));
        return new AudioBufferWriteResult(written, dropped, dropped > 0, dropped);
    }

    public AudioBufferReadResult Read(Span<float> destination, int requestedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedFrames);
        var requestedSamples = checked(requestedFrames * channelCount);
        if (destination.Length < requestedSamples)
            throw new ArgumentException("The sample span does not contain complete interleaved frames.", nameof(destination));
        if (requestedFrames == 0) return default;

        var consumer = consumerSequence;
        var producer = Volatile.Read(ref producerSequence);
        var queued = ClampQueue(producer - consumer);
        var trimmed = Math.Max(0, queued - highWatermarkFrames);
        if (trimmed > 0)
        {
            consumer += trimmed;
            queued -= trimmed;
            Volatile.Write(ref consumerSequence, consumer);
            Interlocked.Increment(ref latencyTrimEvents);
            Interlocked.Add(ref latencyTrimmedFrames, trimmed);
        }

        var liveFrames = Math.Min(requestedFrames, queued);
        if (liveFrames > 0) CopyFromRing(destination, liveFrames, consumer);
        consumer += liveFrames;
        Volatile.Write(ref consumerSequence, consumer);

        var missing = requestedFrames - liveFrames;
        if (missing > 0)
        {
            destination.Slice(checked(liveFrames * channelCount), checked(missing * channelCount)).Clear();
            Interlocked.Increment(ref starvationEvents);
            Interlocked.Add(ref silentFrames, missing);
        }
        RecordQueue(ClampQueue(Volatile.Read(ref producerSequence) - consumer));
        return new AudioBufferReadResult(liveFrames, missing, missing > 0, trimmed);
    }

    internal SpscAudioBufferSnapshot Snapshot()
    {
        long consumerBefore;
        long producer;
        long consumerAfter;
        do
        {
            consumerBefore = Volatile.Read(ref consumerSequence);
            producer = Volatile.Read(ref producerSequence);
            consumerAfter = Volatile.Read(ref consumerSequence);
        }
        while (consumerBefore != consumerAfter);

        var samplesCount = Interlocked.Read(ref queueSampleCount);
        var minimum = Volatile.Read(ref minimumQueuedFrames);
        return new SpscAudioBufferSnapshot(
            producer,
            consumerAfter,
            ClampQueue(producer - consumerAfter),
            minimum == int.MaxValue ? 0 : minimum,
            Volatile.Read(ref maximumQueuedFrames),
            Interlocked.Read(ref queueSampleTotal),
            samplesCount,
            Interlocked.Read(ref starvationEvents),
            Interlocked.Read(ref silentFrames),
            Interlocked.Read(ref physicalCapacityEvents),
            Interlocked.Read(ref physicalCapacityDroppedFrames),
            Interlocked.Read(ref latencyTrimEvents),
            Interlocked.Read(ref latencyTrimmedFrames));
    }

    private void CopyIntoRing(ReadOnlySpan<float> source, int frames, bool silent, long sequence)
    {
        var startFrame = checked((int)(sequence % CapacityFrames));
        var firstFrames = Math.Min(frames, CapacityFrames - startFrame);
        var firstSamples = checked(firstFrames * channelCount);
        var destinationOffset = checked(startFrame * channelCount);
        if (silent) samples.AsSpan(destinationOffset, firstSamples).Clear();
        else source[..firstSamples].CopyTo(samples.AsSpan(destinationOffset, firstSamples));

        var remainingSamples = checked((frames - firstFrames) * channelCount);
        if (remainingSamples == 0) return;
        if (silent) samples.AsSpan(0, remainingSamples).Clear();
        else source.Slice(firstSamples, remainingSamples).CopyTo(samples.AsSpan(0, remainingSamples));
    }

    private void CopyFromRing(Span<float> destination, int frames, long sequence)
    {
        var startFrame = checked((int)(sequence % CapacityFrames));
        var firstFrames = Math.Min(frames, CapacityFrames - startFrame);
        var firstSamples = checked(firstFrames * channelCount);
        samples.AsSpan(checked(startFrame * channelCount), firstSamples).CopyTo(destination);
        var remainingSamples = checked((frames - firstFrames) * channelCount);
        if (remainingSamples > 0) samples.AsSpan(0, remainingSamples).CopyTo(destination[firstSamples..]);
    }

    private int ClampQueue(long difference) => checked((int)Math.Clamp(difference, 0, CapacityFrames));

    private void RecordQueue(int queued)
    {
        Interlocked.Add(ref queueSampleTotal, queued);
        Interlocked.Increment(ref queueSampleCount);
        UpdateMaximum(ref maximumQueuedFrames, queued);
        UpdateMinimum(ref minimumQueuedFrames, queued);
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

    private static void UpdateMinimum(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value < current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}

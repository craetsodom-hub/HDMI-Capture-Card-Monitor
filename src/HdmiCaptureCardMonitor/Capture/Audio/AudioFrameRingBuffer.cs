namespace HdmiCaptureCardMonitor.Capture.Audio;

internal readonly record struct AudioBufferWriteResult(
    int WrittenFrames,
    int DroppedFrames,
    bool OverrunOccurred);

internal readonly record struct AudioBufferReadResult(
    int AudioFrames,
    int SilentFrames,
    bool UnderrunOccurred);

/// <summary>
/// Preallocated complete-frame circular buffer. Packet operations allocate no
/// managed storage and never split an interleaved frame. It is intended to be
/// owned by one audio worker servicing capture and render events.
/// </summary>
internal sealed class AudioFrameRingBuffer : IAudioFrameBuffer
{
    private readonly float[] samples;
    private readonly int channelCount;
    private readonly int highWatermarkFrames;
    private int readFrame;
    private int queuedFrames;

    internal AudioFrameRingBuffer(int capacityFrames, int channelCount, int? highWatermarkFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        var watermark = highWatermarkFrames ?? capacityFrames;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(watermark, nameof(highWatermarkFrames));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, watermark, nameof(highWatermarkFrames));

        samples = new float[checked(capacityFrames * channelCount)];
        CapacityFrames = capacityFrames;
        this.channelCount = channelCount;
        this.highWatermarkFrames = watermark;
    }

    internal int CapacityFrames { get; }
    internal int ChannelCount => channelCount;
    internal int HighWatermarkFrames => highWatermarkFrames;
    internal int QueuedFrames => queuedFrames;
    int IAudioFrameBuffer.QueuedFrames => queuedFrames;
    internal int MaximumQueuedFrames { get; private set; }
    internal long UnderrunCount { get; private set; }
    internal long OverrunCount { get; private set; }
    internal long DroppedFrames { get; private set; }

    internal AudioBufferWriteResult Write(ReadOnlySpan<float> source, int frameCount, bool silent = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        var sampleCount = checked(frameCount * channelCount);
        if (!silent && source.Length < sampleCount)
            throw new ArgumentException("The sample span does not contain the requested number of complete frames.", nameof(source));
        if (frameCount == 0) return default;

        var dropped = 0;
        var sourceFrameOffset = 0;
        if (frameCount > highWatermarkFrames)
        {
            sourceFrameOffset = frameCount - highWatermarkFrames;
            dropped += sourceFrameOffset;
            frameCount = highWatermarkFrames;
            sampleCount = checked(frameCount * channelCount);
        }

        var requiredDrop = Math.Max(0, queuedFrames + frameCount - highWatermarkFrames);
        if (requiredDrop > 0)
        {
            DiscardOldest(requiredDrop);
            dropped += requiredDrop;
        }

        var writeFrame = (readFrame + queuedFrames) % CapacityFrames;
        var firstFrames = Math.Min(frameCount, CapacityFrames - writeFrame);
        var firstSamples = checked(firstFrames * channelCount);
        var sourceOffset = checked(sourceFrameOffset * channelCount);
        var destinationOffset = checked(writeFrame * channelCount);
        if (silent) samples.AsSpan(destinationOffset, firstSamples).Clear();
        else source.Slice(sourceOffset, firstSamples).CopyTo(samples.AsSpan(destinationOffset, firstSamples));

        var remainingSamples = sampleCount - firstSamples;
        if (remainingSamples > 0)
        {
            if (silent) samples.AsSpan(0, remainingSamples).Clear();
            else source.Slice(sourceOffset + firstSamples, remainingSamples).CopyTo(samples.AsSpan(0, remainingSamples));
        }

        queuedFrames += frameCount;
        MaximumQueuedFrames = Math.Max(MaximumQueuedFrames, queuedFrames);
        if (dropped > 0)
        {
            OverrunCount++;
            DroppedFrames += dropped;
        }
        return new AudioBufferWriteResult(frameCount, dropped, dropped > 0);
    }

    AudioBufferWriteResult IAudioFrameBuffer.Write(ReadOnlySpan<float> source, int frameCount, bool silent) =>
        Write(source, frameCount, silent);

    internal AudioBufferReadResult Read(Span<float> destination, int requestedFrames)
    {
        _ = ValidateFrameBuffer(destination.Length, requestedFrames, nameof(destination));
        if (requestedFrames == 0) return default;

        var audioFrames = Math.Min(requestedFrames, queuedFrames);
        var firstFrames = Math.Min(audioFrames, CapacityFrames - readFrame);
        var firstSamples = checked(firstFrames * channelCount);
        samples.AsSpan(checked(readFrame * channelCount), firstSamples).CopyTo(destination);

        var remainingAudioSamples = checked((audioFrames - firstFrames) * channelCount);
        if (remainingAudioSamples > 0)
            samples.AsSpan(0, remainingAudioSamples).CopyTo(destination[firstSamples..]);

        readFrame = (readFrame + audioFrames) % CapacityFrames;
        queuedFrames -= audioFrames;
        var silentFrames = requestedFrames - audioFrames;
        if (silentFrames > 0)
        {
            destination.Slice(checked(audioFrames * channelCount), checked(silentFrames * channelCount)).Clear();
            UnderrunCount++;
        }

        return new AudioBufferReadResult(audioFrames, silentFrames, silentFrames > 0);
    }

    AudioBufferReadResult IAudioFrameBuffer.Read(Span<float> destination, int requestedFrames) =>
        Read(destination, requestedFrames);

    internal void Clear()
    {
        samples.AsSpan().Clear();
        readFrame = 0;
        queuedFrames = 0;
    }

    internal double QueueMilliseconds(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return queuedFrames * 1000d / sampleRate;
    }

    private int ValidateFrameBuffer(int availableSamples, int frameCount, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        var required = checked(frameCount * channelCount);
        if (availableSamples < required)
            throw new ArgumentException("The sample span does not contain the requested number of complete frames.", parameterName);
        return required;
    }

    private void DiscardOldest(int frameCount)
    {
        if (frameCount <= 0 || frameCount > queuedFrames) throw new ArgumentOutOfRangeException(nameof(frameCount));
        readFrame = (readFrame + frameCount) % CapacityFrames;
        queuedFrames -= frameCount;
    }
}

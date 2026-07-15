using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal interface IAudioFrameBuffer
{
    AudioBufferWriteResult Write(ReadOnlySpan<float> source, int frameCount, bool silent = false);
    AudioBufferReadResult Read(Span<float> destination, int requestedFrames);
}

internal interface IAudioGainProcessor
{
    void Process(Span<float> interleavedSamples, int frameCount, int channelCount);
}

internal readonly record struct AudioCapturePacket(
    nint Data,
    uint FrameCount,
    uint Flags,
    ulong DevicePosition,
    ulong QpcPosition);

internal interface IAudioCaptureBufferAccess
{
    uint GetNextPacketSize();
    AudioCapturePacket GetBuffer();
    void ReleaseBuffer(uint frameCount);
}

internal interface IAudioRenderBufferAccess
{
    uint GetCurrentPadding();
    nint GetBuffer(uint frameCount);
    void ReleaseBuffer(uint frameCount, uint flags);
}

internal readonly record struct AudioCaptureBatchResult(
    long FramesCaptured,
    long SilentFrames,
    long Discontinuities,
    long TimestampErrors,
    ulong LastDevicePosition,
    ulong LastQpcPosition,
    bool HasPacket);

internal readonly record struct AudioRenderPacketResult(bool Rendered, int Frames, int SilentFrames);

internal static class AudioNativeBufferProcessor
{
    internal static unsafe AudioCaptureBatchResult ProcessCapture(
        IAudioCaptureBufferAccess access,
        IAudioFrameBuffer ring,
        AudioStreamFormat format)
    {
        long captured = 0;
        long silentFrames = 0;
        long discontinuities = 0;
        long timestampErrors = 0;
        ulong lastDevicePosition = 0;
        ulong lastQpcPosition = 0;
        var hasPacket = false;

        var packetFrames = access.GetNextPacketSize();
        while (packetFrames > 0)
        {
            var packet = access.GetBuffer();
            var releaseRequired = packet.FrameCount > 0;
            try
            {
                var count = checked((int)packet.FrameCount);
                if (count == 0)
                {
                    packetFrames = access.GetNextPacketSize();
                    continue;
                }

                var flags = (_AUDCLNT_BUFFERFLAGS)packet.Flags;
                var silent = (flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                ReadOnlySpan<float> samples;
                if (silent)
                {
                    samples = ReadOnlySpan<float>.Empty;
                }
                else
                {
                    if (packet.Data == 0)
                        throw new AudioSessionException(
                            AudioMonitorFailureCategory.BufferFailure,
                            "Windows returned an invalid audio capture packet.");
                    var sampleCount = format.FramesToSampleCount(count);
                    samples = new ReadOnlySpan<float>((void*)packet.Data, sampleCount);
                }

                ring.Write(samples, count, silent);
                captured += packet.FrameCount;
                if (silent) silentFrames += packet.FrameCount;
                if ((flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) discontinuities++;
                if ((flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) timestampErrors++;
                lastDevicePosition = packet.DevicePosition;
                lastQpcPosition = packet.QpcPosition;
                hasPacket = true;
            }
            catch (Exception exception) when (exception is not AudioSessionException)
            {
                throw new AudioSessionException(
                    AudioMonitorFailureCategory.BufferFailure,
                    "Audio capture packet processing failed safely.",
                    exception);
            }
            finally
            {
                if (releaseRequired) access.ReleaseBuffer(packet.FrameCount);
            }

            packetFrames = access.GetNextPacketSize();
        }

        return new AudioCaptureBatchResult(
            captured,
            silentFrames,
            discontinuities,
            timestampErrors,
            lastDevicePosition,
            lastQpcPosition,
            hasPacket);
    }

    internal static unsafe void PrefillRenderWithSilence(
        IAudioRenderBufferAccess access,
        int channelCount,
        uint bufferFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (bufferFrames == 0) return;

        var data = access.GetBuffer(bufferFrames);
        var releaseFlags = (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT;
        try
        {
            if (data == 0)
                throw new AudioSessionException(
                    AudioMonitorFailureCategory.BufferFailure,
                    "Windows returned an invalid audio render buffer.");
            var sampleCount = checked((int)bufferFrames * channelCount);
            new Span<float>((void*)data, sampleCount).Clear();
            releaseFlags = 0;
        }
        catch (Exception exception) when (exception is not AudioSessionException)
        {
            throw new AudioSessionException(
                AudioMonitorFailureCategory.BufferFailure,
                "Audio render prefill failed safely.",
                exception);
        }
        finally
        {
            access.ReleaseBuffer(bufferFrames, releaseFlags);
        }
    }

    internal static unsafe AudioRenderPacketResult ProcessRender(
        IAudioRenderBufferAccess access,
        IAudioFrameBuffer ring,
        AudioStreamFormat format,
        IAudioGainProcessor gain,
        uint bufferFrames,
        int renderPeriodFrames)
    {
        var padding = access.GetCurrentPadding();
        if (padding >= bufferFrames) return default;
        var available = bufferFrames - padding;
        var period = checked((uint)Math.Max(1, renderPeriodFrames));
        if (available < period) return default;

        available = period;
        var data = access.GetBuffer(available);
        var releaseFlags = (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT;
        try
        {
            if (data == 0)
                throw new AudioSessionException(
                    AudioMonitorFailureCategory.BufferFailure,
                    "Windows returned an invalid audio render buffer.");
            var frameCount = checked((int)available);
            var sampleCount = format.FramesToSampleCount(frameCount);
            var samples = new Span<float>((void*)data, sampleCount);
            var result = ring.Read(samples, frameCount);
            gain.Process(samples, frameCount, format.ChannelCount);
            releaseFlags = 0;
            return new AudioRenderPacketResult(true, frameCount, result.SilentFrames);
        }
        catch (Exception exception) when (exception is not AudioSessionException)
        {
            throw new AudioSessionException(
                AudioMonitorFailureCategory.BufferFailure,
                "Audio render packet processing failed safely.",
                exception);
        }
        finally
        {
            access.ReleaseBuffer(available, releaseFlags);
        }
    }
}

internal sealed class WasapiCaptureBufferAccess(IAudioCaptureClient service) : IAudioCaptureBufferAccess
{
    private readonly IAudioCaptureClient service = service ?? throw new ArgumentNullException(nameof(service));

    public uint GetNextPacketSize()
    {
        service.GetNextPacketSize(out var frames);
        return frames;
    }

    public unsafe AudioCapturePacket GetBuffer()
    {
        service.GetBuffer(out var data, out var frames, out var flags, out var devicePosition, out var qpcPosition);
        return new AudioCapturePacket((nint)data, frames, flags, devicePosition, qpcPosition);
    }

    public void ReleaseBuffer(uint frameCount) => service.ReleaseBuffer(frameCount);
}

internal sealed class WasapiRenderBufferAccess(IAudioClient client, IAudioRenderClient service) : IAudioRenderBufferAccess
{
    private readonly IAudioClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IAudioRenderClient service = service ?? throw new ArgumentNullException(nameof(service));

    public uint GetCurrentPadding()
    {
        client.GetCurrentPadding(out var padding);
        return padding;
    }

    public unsafe nint GetBuffer(uint frameCount)
    {
        service.GetBuffer(frameCount, out var data);
        return (nint)data;
    }

    public void ReleaseBuffer(uint frameCount, uint flags) => service.ReleaseBuffer(frameCount, flags);
}

internal sealed class AudioSessionException(
    AudioMonitorFailureCategory category,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal AudioMonitorFailureCategory Category { get; } = category;
}

using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Capture.Audio;
using Windows.Win32.Media.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioNativeSafetyTests
{
    private static readonly AudioStreamFormat Stereo = AudioStreamFormat.CreateIeeeFloat(48_000, 2, 3);

    [Fact]
    public void SilentCaptureWithNullPointerWritesExactZeroFramesWithoutDereference()
    {
        var access = new FakeCaptureAccess(new AudioCapturePacket(
            0, 48, (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT, 10, 20));
        var ring = new RecordingFrameBuffer();

        var result = AudioNativeBufferProcessor.ProcessCapture(access, ring, Stereo);

        Assert.Equal(48, result.FramesCaptured);
        Assert.Equal(48, result.SilentFrames);
        Assert.Equal(48, ring.WrittenFrames);
        Assert.True(ring.LastWriteWasSilent);
        Assert.True(ring.LastSourceWasEmpty);
        Assert.Equal(1, access.ReleaseCalls);
    }

    [Fact]
    public void SilentCaptureWithGarbagePointerNeverAccessesPointerData()
    {
        var access = new FakeCaptureAccess(new AudioCapturePacket(
            new nint(1), 24, (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT, 0, 0));
        var ring = new RecordingFrameBuffer();

        _ = AudioNativeBufferProcessor.ProcessCapture(access, ring, Stereo);

        Assert.True(ring.LastSourceWasEmpty);
        Assert.Equal(1, access.ReleaseCalls);
    }

    [Fact]
    public void DiscontinuityCallbackRecordsPacketAndExactQueueChange()
    {
        var packet = new AudioCapturePacket(
            0, 48,
            (uint)(_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT |
                _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY),
            123, 456);
        var access = new FakeCaptureAccess(packet);
        var ring = new RecordingFrameBuffer();
        (AudioCapturePacket Packet, int Before, int After)? observed = null;

        var result = AudioNativeBufferProcessor.ProcessCapture(
            access, ring, Stereo,
            (value, before, after) => observed = (value, before, after));

        Assert.Equal(1, result.Discontinuities);
        Assert.NotNull(observed);
        Assert.Equal(packet, observed.Value.Packet);
        Assert.Equal(0, observed.Value.Before);
        Assert.Equal(48, observed.Value.After);
        Assert.Equal(1, access.ReleaseCalls);
    }

    [Fact]
    public void NonSilentCaptureWithNullPointerIsTypedAndReleasedExactlyOnce()
    {
        var access = new FakeCaptureAccess(new AudioCapturePacket(0, 12, 0, 0, 0));

        var error = Assert.Throws<AudioSessionException>(() =>
            AudioNativeBufferProcessor.ProcessCapture(access, new RecordingFrameBuffer(), Stereo));

        Assert.Equal(AudioMonitorFailureCategory.BufferFailure, error.Category);
        Assert.Equal(1, access.ReleaseCalls);
    }

    [Fact]
    public void ZeroFrameCapturePacketDoesNotCreateOrReleaseNativeOwnership()
    {
        var access = new FakeCaptureAccess(new AudioCapturePacket(0, 0, 0, 0, 0));
        var ring = new RecordingFrameBuffer();

        var result = AudioNativeBufferProcessor.ProcessCapture(access, ring, Stereo);

        Assert.Equal(0, result.FramesCaptured);
        Assert.Equal(0, access.ReleaseCalls);
        Assert.Equal(0, ring.WriteCalls);
    }

    [Fact]
    public void CaptureRingFailureStillReleasesPacketExactlyOnce()
    {
        using var memory = new UnmanagedAudioBuffer(8 * sizeof(float));
        var access = new FakeCaptureAccess(new AudioCapturePacket(memory.Pointer, 4, 0, 0, 0));
        var ring = new RecordingFrameBuffer { WriteFailure = new InvalidOperationException("ring") };

        var error = Assert.Throws<AudioSessionException>(() =>
            AudioNativeBufferProcessor.ProcessCapture(access, ring, Stereo));
        Assert.Equal(AudioMonitorFailureCategory.BufferFailure, error.Category);
        Assert.Equal(1, access.ReleaseCalls);
    }

    [Fact]
    public void RenderRingFailureReleasesAcquiredPacketOnceAsSilence() =>
        AssertRenderFailureReleased(new RecordingFrameBuffer { ReadFailure = new InvalidOperationException("ring") }, new RecordingGain());

    [Fact]
    public void RenderGainFailureReleasesAcquiredPacketOnceAsSilence() =>
        AssertRenderFailureReleased(new RecordingFrameBuffer(), new RecordingGain { Failure = new InvalidOperationException("gain") });

    [Fact]
    public void RenderCheckedSizingFailureReleasesAcquiredPacketOnceAsSilence()
    {
        using var memory = new UnmanagedAudioBuffer(sizeof(float));
        var access = new FakeRenderAccess(memory.Pointer, uint.MaxValue, 0);
        var format = AudioStreamFormat.CreateIeeeFloat(48_000, 2);

        var error = Assert.Throws<AudioSessionException>(() => AudioNativeBufferProcessor.ProcessRender(
            access, new RecordingFrameBuffer(), format, new RecordingGain(), uint.MaxValue, int.MaxValue));
        Assert.Equal(AudioMonitorFailureCategory.BufferFailure, error.Category);

        Assert.Equal(1, access.ReleaseCalls);
        Assert.Equal((uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT, access.LastReleaseFlags);
    }

    [Fact]
    public void NormalRenderProcessingReleasesPacketExactlyOnceWithoutSilentFlag()
    {
        using var memory = new UnmanagedAudioBuffer(8 * sizeof(float));
        var access = new FakeRenderAccess(memory.Pointer, 4, 0);
        var ring = new RecordingFrameBuffer();
        var gain = new RecordingGain();

        var result = AudioNativeBufferProcessor.ProcessRender(access, ring, Stereo, gain, 4, 4);

        Assert.True(result.Rendered);
        Assert.Equal(1, access.ReleaseCalls);
        Assert.Equal(0u, access.LastReleaseFlags);
        Assert.Equal(1, ring.ReadCalls);
        Assert.Equal(1, gain.Calls);
    }

    [Fact]
    public void InitialPrefillFailureAfterAcquisitionReleasesPacketExactlyOnceAsSilence()
    {
        using var memory = new UnmanagedAudioBuffer(sizeof(float));
        var access = new FakeRenderAccess(memory.Pointer, 2, 0);

        var error = Assert.Throws<AudioSessionException>(() =>
            AudioNativeBufferProcessor.PrefillRenderWithSilence(access, int.MaxValue, 2));
        Assert.Equal(AudioMonitorFailureCategory.BufferFailure, error.Category);

        Assert.Equal(1, access.ReleaseCalls);
        Assert.Equal((uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT, access.LastReleaseFlags);
    }

    private static void AssertRenderFailureReleased(RecordingFrameBuffer ring, RecordingGain gain)
    {
        using var memory = new UnmanagedAudioBuffer(8 * sizeof(float));
        var access = new FakeRenderAccess(memory.Pointer, 4, 0);

        var error = Assert.Throws<AudioSessionException>(() =>
            AudioNativeBufferProcessor.ProcessRender(access, ring, Stereo, gain, 4, 4));
        Assert.Equal(AudioMonitorFailureCategory.BufferFailure, error.Category);

        Assert.Equal(1, access.ReleaseCalls);
        Assert.Equal((uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT, access.LastReleaseFlags);
    }

    private sealed class FakeCaptureAccess(AudioCapturePacket packet) : IAudioCaptureBufferAccess
    {
        private int nextCalls;
        public int ReleaseCalls { get; private set; }
        public uint GetNextPacketSize() => nextCalls++ == 0 ? 1u : 0u;
        public AudioCapturePacket GetBuffer() => packet;
        public void ReleaseBuffer(uint frameCount) { _ = frameCount; ReleaseCalls++; }
    }

    private sealed class FakeRenderAccess(nint pointer, uint bufferFrames, uint padding) : IAudioRenderBufferAccess
    {
        public int ReleaseCalls { get; private set; }
        public uint LastReleaseFlags { get; private set; }
        public uint GetCurrentPadding() => padding;
        public nint GetBuffer(uint frameCount) { Assert.True(frameCount <= bufferFrames); return pointer; }
        public void ReleaseBuffer(uint frameCount, uint flags) { _ = frameCount; ReleaseCalls++; LastReleaseFlags = flags; }
    }

    private sealed class RecordingFrameBuffer : IAudioFrameBuffer
    {
        public Exception? WriteFailure { get; init; }
        public Exception? ReadFailure { get; init; }
        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WrittenFrames { get; private set; }
        public bool LastWriteWasSilent { get; private set; }
        public bool LastSourceWasEmpty { get; private set; }
        public int QueuedFrames => WrittenFrames;

        public AudioBufferWriteResult Write(ReadOnlySpan<float> source, int frameCount, bool silent = false)
        {
            WriteCalls++;
            if (WriteFailure is not null) throw WriteFailure;
            WrittenFrames = frameCount;
            LastWriteWasSilent = silent;
            LastSourceWasEmpty = source.IsEmpty;
            return new AudioBufferWriteResult(frameCount, 0, false);
        }

        public AudioBufferReadResult Read(Span<float> destination, int requestedFrames)
        {
            ReadCalls++;
            if (ReadFailure is not null) throw ReadFailure;
            destination.Clear();
            return new AudioBufferReadResult(requestedFrames, 0, false);
        }
    }

    private sealed class RecordingGain : IAudioGainProcessor
    {
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public void Process(Span<float> interleavedSamples, int frameCount, int channelCount)
        {
            _ = interleavedSamples;
            _ = frameCount;
            _ = channelCount;
            Calls++;
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class UnmanagedAudioBuffer : IDisposable
    {
        public UnmanagedAudioBuffer(int bytes) => Pointer = Marshal.AllocHGlobal(bytes);
        public nint Pointer { get; }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}

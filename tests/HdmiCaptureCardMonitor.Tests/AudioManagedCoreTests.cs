using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioManagedCoreTests
{
    [Theory]
    [InlineData(AudioEndpointDataFlow.Capture, "Unnamed audio input")]
    [InlineData(AudioEndpointDataFlow.Render, "Unnamed audio output")]
    public void EndpointUsesSafeFallbackNameAndNeverFormatsOpaqueId(
        AudioEndpointDataFlow flow,
        string expectedName)
    {
        const string opaqueId = "opaque-{0.0.1.00000000}.secret-endpoint";
        var endpoint = new AudioEndpoint(opaqueId, "  ", flow);

        Assert.Equal(opaqueId, endpoint.Id);
        Assert.Equal(expectedName, endpoint.DisplayName);
        Assert.Equal(expectedName, endpoint.ToString());
        Assert.DoesNotContain(opaqueId, endpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointNormalizesOptionalMetadataWithoutParsingId()
    {
        var container = Guid.NewGuid();
        var endpoint = new AudioEndpoint(
            "unparsed:endpoint/id",
            "  Headphones  ",
            AudioEndpointDataFlow.Render,
            "  USB Audio Device ",
            " ",
            container);

        Assert.Equal("unparsed:endpoint/id", endpoint.Id);
        Assert.Equal("Headphones", endpoint.DisplayName);
        Assert.Equal("USB Audio Device", endpoint.DeviceDescription);
        Assert.Null(endpoint.InterfaceFriendlyName);
        Assert.Equal(container, endpoint.ContainerId);
    }

    [Fact]
    public void DuplicateEndpointNamesAreDisambiguatedWithoutOpaqueIds()
    {
        var endpoints = CoreAudioEndpointDiscoveryService.Disambiguate([
            new AudioEndpoint("opaque-b", "USB Audio", AudioEndpointDataFlow.Render),
            new AudioEndpoint("opaque-a", "USB Audio", AudioEndpointDataFlow.Render)
        ]);

        Assert.Equal(["USB Audio (1)", "USB Audio (2)"], endpoints.Select(endpoint => endpoint.DisplayName));
        Assert.All(endpoints, endpoint => Assert.DoesNotContain(endpoint.Id, endpoint.DisplayName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), AudioMonitorFailureCategory.AccessDenied)]
    [InlineData(unchecked((int)0x88890004), AudioMonitorFailureCategory.DeviceInvalidated)]
    [InlineData(unchecked((int)0x88890010), AudioMonitorFailureCategory.AudioServiceNotRunning)]
    [InlineData(unchecked((int)0x80004005), AudioMonitorFailureCategory.EndpointCreateFailed)]
    public void EndpointDiscoveryMapsKnownFailuresPrecisely(int hresult, AudioMonitorFailureCategory expected) =>
        Assert.Equal(expected, CoreAudioEndpointDiscoveryService.MapFailure(hresult));

    [Fact]
    public void StartRequestUsesNullForSystemDefaultAndSafeCustomerFormatting()
    {
        const string opaqueId = "opaque-capture-id";
        var input = new AudioEndpoint(opaqueId, "Laptop microphone", AudioEndpointDataFlow.Capture);
        var request = new AudioMonitorStartRequest(input, null);

        Assert.True(request.UsesSystemDefaultOutput);
        Assert.Null(request.RenderEndpoint);
        Assert.Contains("System default output", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(opaqueId, request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StartRequestRejectsEndpointFlowMixups()
    {
        var capture = new AudioEndpoint("capture", "Input", AudioEndpointDataFlow.Capture);
        var render = new AudioEndpoint("render", "Output", AudioEndpointDataFlow.Render);

        Assert.Throws<ArgumentException>(() => new AudioMonitorStartRequest(render, null));
        Assert.Throws<ArgumentException>(() => new AudioMonitorStartRequest(capture, capture));
        Assert.NotNull(new AudioMonitorStartRequest(capture, render));
    }

    [Fact]
    public void FloatFormatDerivesCheckedFrameMetadata()
    {
        var format = AudioStreamFormat.CreateIeeeFloat(48000, 2, 3);

        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(8, format.BlockAlignment);
        Assert.Equal(384000, format.AverageBytesPerSecond);
        Assert.Equal(6, format.FramesToSampleCount(3));
        Assert.Equal(24, format.FramesToByteCount(3));
        Assert.Equal(10, format.FramesToMilliseconds(480), 6);
        Assert.Contains("IEEE float", format.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(48000, 0)]
    [InlineData(-1, 2)]
    public void FloatFormatRejectsInvalidCoreFields(int sampleRate, int channels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioStreamFormat.CreateIeeeFloat(sampleRate, channels));

    [Fact]
    public void FormatAndBufferSizingUseCheckedArithmetic()
    {
        Assert.Throws<OverflowException>(() => AudioStreamFormat.CreateIeeeFloat(int.MaxValue, 2));
        Assert.Throws<OverflowException>(() => AudioStreamFormat.CreateIeeeFloat(48000, 20000));
        var format = AudioStreamFormat.CreateIeeeFloat(48000, 2);
        Assert.Throws<OverflowException>(() => format.FramesToSampleCount(int.MaxValue));
        Assert.Throws<OverflowException>(() => new AudioFrameRingBuffer(int.MaxValue, 2));
    }

    [Fact]
    public void RingBufferPreservesCompleteFramesAcrossWraparound()
    {
        var buffer = new AudioFrameRingBuffer(4, 2);
        buffer.Write([1, 10, 2, 20, 3, 30], 3);
        Span<float> first = stackalloc float[4];
        var firstRead = buffer.Read(first, 2);
        buffer.Write([4, 40, 5, 50, 6, 60], 3);
        Span<float> second = stackalloc float[8];
        var secondRead = buffer.Read(second, 4);

        Assert.Equal([1f, 10f, 2f, 20f], first.ToArray());
        Assert.Equal(2, firstRead.AudioFrames);
        Assert.Equal([3f, 30f, 4f, 40f, 5f, 50f, 6f, 60f], second.ToArray());
        Assert.Equal(4, secondRead.AudioFrames);
        Assert.Equal(0, buffer.QueuedFrames);
        Assert.Equal(4, buffer.MaximumQueuedFrames);
    }

    [Fact]
    public void ExactCapacityDoesNotCountAsOverrun()
    {
        var buffer = new AudioFrameRingBuffer(3, 1);

        var result = buffer.Write([1, 2, 3], 3);

        Assert.False(result.OverrunOccurred);
        Assert.Equal(3, result.WrittenFrames);
        Assert.Equal(3, buffer.QueuedFrames);
        Assert.Equal(0, buffer.OverrunCount);
    }

    [Fact]
    public void OverrunDiscardsOldestCompleteFramesAndBoundsLatency()
    {
        var buffer = new AudioFrameRingBuffer(4, 2);
        buffer.Write([1, 10, 2, 20, 3, 30], 3);

        var result = buffer.Write([4, 40, 5, 50, 6, 60], 3);
        Span<float> output = stackalloc float[8];
        buffer.Read(output, 4);

        Assert.True(result.OverrunOccurred);
        Assert.Equal(2, result.DroppedFrames);
        Assert.Equal(1, buffer.OverrunCount);
        Assert.Equal(2, buffer.DroppedFrames);
        Assert.Equal([3f, 30f, 4f, 40f, 5f, 50f, 6f, 60f], output.ToArray());
    }

    [Fact]
    public void PacketLargerThanCapacityKeepsOnlyNewestFrames()
    {
        var buffer = new AudioFrameRingBuffer(3, 1);

        var result = buffer.Write([1, 2, 3, 4, 5], 5);
        Span<float> output = stackalloc float[3];
        buffer.Read(output, 3);

        Assert.Equal(3, result.WrittenFrames);
        Assert.Equal(2, result.DroppedFrames);
        Assert.Equal([3f, 4f, 5f], output.ToArray());
    }

    [Fact]
    public void HighWatermarkDropsOldestBeforeHardCapacity()
    {
        var buffer = new AudioFrameRingBuffer(6, 1, highWatermarkFrames: 4);
        buffer.Write([1, 2, 3, 4], 4);

        var result = buffer.Write([5, 6], 2);
        Span<float> output = stackalloc float[4];
        buffer.Read(output, 4);

        Assert.Equal(2, result.DroppedFrames);
        Assert.Equal([3f, 4f, 5f, 6f], output.ToArray());
        Assert.True(buffer.MaximumQueuedFrames <= 4);
    }

    [Fact]
    public void UnderrunRendersAvailableFramesThenSilence()
    {
        var buffer = new AudioFrameRingBuffer(4, 2);
        buffer.Write([0.25f, -0.25f], 1);
        Span<float> output = stackalloc float[6];

        var result = buffer.Read(output, 3);

        Assert.True(result.UnderrunOccurred);
        Assert.Equal(1, result.AudioFrames);
        Assert.Equal(2, result.SilentFrames);
        Assert.Equal([0.25f, -0.25f, 0f, 0f, 0f, 0f], output.ToArray());
        Assert.Equal(1, buffer.UnderrunCount);
    }

    [Fact]
    public void SilentCapturePacketWritesZeroWithoutReadingSourceValues()
    {
        var buffer = new AudioFrameRingBuffer(2, 2);
        buffer.Write([float.NaN, float.PositiveInfinity, 1, 1], 2, silent: true);
        Span<float> output = stackalloc float[4];

        var result = buffer.Read(output, 2);

        Assert.False(result.UnderrunOccurred);
        Assert.Equal([0f, 0f, 0f, 0f], output.ToArray());
    }

    [Fact]
    public void SilentCapturePacketAcceptsNoSourceMemory()
    {
        var buffer = new AudioFrameRingBuffer(2, 2);

        var write = buffer.Write(ReadOnlySpan<float>.Empty, 2, silent: true);
        Span<float> output = stackalloc float[4];
        var read = buffer.Read(output, 2);

        Assert.Equal(2, write.WrittenFrames);
        Assert.False(read.UnderrunOccurred);
        Assert.Equal([0f, 0f, 0f, 0f], output.ToArray());
    }

    [Fact]
    public void BufferRejectsPartialFrameSpansAndInvalidWatermarks()
    {
        var buffer = new AudioFrameRingBuffer(4, 2);

        Assert.Throws<ArgumentException>(() => buffer.Write([1, 2, 3], 2));
        Assert.Throws<ArgumentException>(() => buffer.Read(new float[3], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameRingBuffer(4, 2, 5));
    }

    [Fact]
    public void QueueDepthCalculationUsesFramesNotSamples()
    {
        var buffer = new AudioFrameRingBuffer(960, 2);
        buffer.Write(new float[960], 480);

        Assert.Equal(10, buffer.QueueMilliseconds(48000), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.QueueMilliseconds(0));
    }

    [Fact]
    public void ClearDropsQueuedAudioWithoutResettingLifetimeCounters()
    {
        var buffer = new AudioFrameRingBuffer(2, 1);
        buffer.Write([1, 2, 3], 3);
        Assert.True(buffer.DroppedFrames > 0);

        buffer.Clear();

        Assert.Equal(0, buffer.QueuedFrames);
        Assert.True(buffer.DroppedFrames > 0);
        Span<float> output = stackalloc float[1];
        Assert.True(buffer.Read(output, 1).UnderrunOccurred);
    }

    [Fact]
    public void DefaultHundredPercentGainLeavesFiniteSamplesUnchanged()
    {
        var gain = new AudioGainController(rampFrames: 4);
        Span<float> samples = [1, -0.5f, 0.25f, -1];

        gain.Process(samples, frameCount: 2, channelCount: 2);

        Assert.Equal([1f, -0.5f, 0.25f, -1f], samples.ToArray());
        Assert.Equal(100, gain.VolumePercent, 6);
        Assert.False(gain.IsMuted);
    }

    [Fact]
    public void FiftyPercentGainRampsPerFrameAndPreservesChannelBalance()
    {
        var gain = new AudioGainController(rampFrames: 4);
        gain.SetVolume(50);
        Span<float> samples = [1, 1, 1, 1, 1, 1, 1, 1];

        gain.Process(samples, frameCount: 4, channelCount: 2);

        Assert.Equal(samples[0], samples[1]);
        Assert.Equal(samples[2], samples[3]);
        Assert.True(samples[0] > samples[2]);
        Assert.True(samples[2] > samples[4]);
        Assert.True(samples[4] > samples[6]);
        Assert.Equal(0.5f, samples[6], 6);
        Assert.Equal(0.5f, gain.CurrentGain, 6);
    }

    [Fact]
    public void ZeroVolumeAndMuteReachSilenceThroughShortRamp()
    {
        var volume = new AudioGainController(rampFrames: 2);
        volume.SetVolume(0);
        Span<float> volumeSamples = [1, 1];
        volume.Process(volumeSamples, 2, 1);

        var muted = new AudioGainController(rampFrames: 2);
        muted.SetMuted(true);
        Span<float> mutedSamples = [1, 1];
        muted.Process(mutedSamples, 2, 1);

        Assert.Equal(0f, volumeSamples[^1]);
        Assert.Equal(0f, mutedSamples[^1]);
        Assert.True(muted.IsMuted);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-5, 0)]
    [InlineData(150, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    public void VolumeTargetClampsInvalidValues(double requested, double expected)
    {
        var gain = new AudioGainController();

        gain.SetVolume(requested);

        Assert.Equal(expected, gain.VolumePercent, 6);
    }

    [Fact]
    public void ProcessorNeverEmitsNanOrInfinity()
    {
        var gain = new AudioGainController(rampFrames: 1);
        gain.SetVolume(double.PositiveInfinity);
        Span<float> samples = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue];

        gain.Process(samples, frameCount: 2, channelCount: 2);

        Assert.All(samples.ToArray(), value => Assert.True(float.IsFinite(value)));
        Assert.Equal([0f, 0f, 0f, float.MaxValue], samples.ToArray());
    }

    [Fact]
    public void UnmuteProcessesCurrentLiveSamplesRatherThanReplayingOldData()
    {
        var gain = new AudioGainController(rampFrames: 1);
        gain.SetMuted(true);
        Span<float> oldPacket = [0.75f];
        gain.Process(oldPacket, 1, 1);
        gain.SetMuted(false);
        Span<float> livePacket = [0.25f];
        gain.Process(livePacket, 1, 1);

        Assert.Equal(0f, oldPacket[0]);
        Assert.Equal(0.25f, livePacket[0]);
    }

    [Fact]
    public void AtomicVolumeAndMuteUpdatesAlwaysProduceFiniteBoundedTarget()
    {
        var gain = new AudioGainController(rampFrames: 1);

        Parallel.For(0, 1000, index =>
        {
            gain.SetVolume(index % 2 == 0 ? -50 : 150);
            gain.SetMuted(index % 3 == 0);
        });

        Assert.InRange(gain.VolumePercent, 0, 100);
        Span<float> sample = [1f];
        gain.Process(sample, 1, 1);
        Assert.True(float.IsFinite(sample[0]));
        Assert.InRange(sample[0], 0, 1);
    }

    [Fact]
    public void DiagnosticsCalculateQueueAndPeriodDurationsWithoutEndpointIds()
    {
        const string opaqueId = "opaque-id-that-must-not-appear";
        var format = AudioStreamFormat.CreateIeeeFloat(48000, 2);
        var diagnostics = new AudioMonitorDiagnostics(
            Guid.NewGuid(), "Microphone", "Headphones", true,
            "float 48 kHz", "float 48 kHz", format,
            AudioMonitorInitializationPath.AudioClient3,
            480, 240, 960, 480, 480, 720,
            1000, 900, 20, 1, 2, 3, 4, 5,
            75, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        Assert.Equal(10, diagnostics.CurrentQueueMilliseconds, 6);
        Assert.Equal(10, diagnostics.CapturePeriodMilliseconds, 6);
        Assert.Equal(5, diagnostics.RenderPeriodMilliseconds, 6);
        Assert.DoesNotContain(opaqueId, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("end-to-end", diagnostics.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResultsAndFailuresKeepTechnicalDetailsOutOfCustomerFormatting()
    {
        var exception = new InvalidOperationException("secret COM exception");
        var failure = new AudioMonitorFailure(
            AudioMonitorFailureCategory.AccessDenied,
            "Enable microphone access for desktop apps.",
            unchecked((int)0x80070005),
            exception);
        var result = AudioMonitorStartResult.Failed(Guid.NewGuid(), failure);

        Assert.Equal(failure.CustomerMessage, failure.ToString());
        Assert.Equal(failure.CustomerMessage, result.ToString());
        Assert.DoesNotContain("80070005", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(exception.Message, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(960, 960, 480, 0)]
    [InlineData(1920, 960, 480, 3000)]
    [InlineData(480, 960, 480, -2000)]
    [InlineData(2880, 960, 480, 3000)]
    [InlineData(0, 960, 480, -3000)]
    public void QueueRateCorrectionIsProportionalAndBounded(
        int queued,
        int target,
        int period,
        double expectedPpm) =>
        Assert.Equal(expectedPpm, AudioQueueRateControllerMath.CalculateAdjustmentPpm(queued, target, period), 6);

    [Fact]
    public void QueueRateCorrectionRejectsInvalidMeasurements()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioQueueRateControllerMath.CalculateAdjustmentPpm(-1, 960, 480));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioQueueRateControllerMath.CalculateAdjustmentPpm(0, 960, 0));
    }
}

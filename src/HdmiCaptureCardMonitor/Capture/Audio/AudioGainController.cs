namespace HdmiCaptureCardMonitor.Capture.Audio;

/// <summary>
/// Lock-free control target with audio-thread-owned ramp state. Volume and mute
/// occupy one atomic word so the packet loop observes a coherent target.
/// </summary>
internal sealed class AudioGainController : IAudioGainProcessor
{
    private const int GainScale = 1_000_000;
    private const long MutedMask = 1L << 32;
    private const long GainMask = uint.MaxValue;

    private readonly int rampFrames;
    private long encodedTarget = GainScale;
    private long appliedTarget = GainScale;
    private float currentGain = 1f;
    private float rampDestination = 1f;
    private int rampRemaining;

    internal AudioGainController(int rampFrames = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rampFrames);
        this.rampFrames = rampFrames;
    }

    internal double VolumePercent => DecodeGain(Volatile.Read(ref encodedTarget)) * 100d;
    internal bool IsMuted => (Volatile.Read(ref encodedTarget) & MutedMask) != 0;
    internal float CurrentGain => currentGain;

    internal void SetVolume(double volumePercent)
    {
        var encodedGain = checked((uint)Math.Round(ClampVolumePercent(volumePercent) / 100d * GainScale, MidpointRounding.AwayFromZero));
        while (true)
        {
            var current = Volatile.Read(ref encodedTarget);
            var next = (current & MutedMask) | encodedGain;
            if (Interlocked.CompareExchange(ref encodedTarget, next, current) == current) return;
        }
    }

    internal void SetMuted(bool muted)
    {
        while (true)
        {
            var current = Volatile.Read(ref encodedTarget);
            var next = muted ? current | MutedMask : current & ~MutedMask;
            if (Interlocked.CompareExchange(ref encodedTarget, next, current) == current) return;
        }
    }

    internal void Process(Span<float> interleavedSamples, int frameCount, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        var requiredSamples = checked(frameCount * channelCount);
        if (interleavedSamples.Length < requiredSamples)
            throw new ArgumentException("The sample span does not contain the requested number of complete frames.", nameof(interleavedSamples));

        var target = Volatile.Read(ref encodedTarget);
        if (target != appliedTarget)
        {
            appliedTarget = target;
            rampDestination = (target & MutedMask) != 0 ? 0f : DecodeGain(target);
            rampRemaining = rampFrames;
        }

        var sampleIndex = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (rampRemaining > 0)
            {
                currentGain += (rampDestination - currentGain) / rampRemaining;
                rampRemaining--;
                if (rampRemaining == 0) currentGain = rampDestination;
            }

            if (!float.IsFinite(currentGain)) currentGain = 0f;
            for (var channel = 0; channel < channelCount; channel++, sampleIndex++)
            {
                var input = interleavedSamples[sampleIndex];
                var output = float.IsFinite(input) ? input * currentGain : 0f;
                interleavedSamples[sampleIndex] = float.IsFinite(output) ? output : 0f;
            }
        }
    }

    void IAudioGainProcessor.Process(Span<float> interleavedSamples, int frameCount, int channelCount) =>
        Process(interleavedSamples, frameCount, channelCount);

    internal static double ClampVolumePercent(double value)
    {
        if (double.IsNaN(value) || double.IsNegativeInfinity(value)) return 0;
        if (double.IsPositiveInfinity(value)) return 100;
        return Math.Clamp(value, 0, 100);
    }

    private static float DecodeGain(long encoded) =>
        (float)((encoded & GainMask) / (double)GainScale);
}

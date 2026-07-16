using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioInitializationCoordinatorTests
{
    [Fact]
    public void HybridSuccessUsesOneFreshAttempt()
    {
        var factory = new RecordingAttemptFactory();

        var result = AudioInitializationCoordinator.Initialize(
            true, factory.Succeed, _ => true);

        Assert.Equal(AudioInitializationMode.Hybrid, result);
        Assert.Equal([AudioInitializationMode.Hybrid], factory.Attempts);
        Assert.Equal(0, factory.ReleaseCount);
    }

    [Fact]
    public void AudioClient3UnavailableCreatesFreshClassicAttempt()
    {
        var factory = new RecordingAttemptFactory { HybridFailure = new AudioInitializationFallbackException("unavailable") };

        var result = AudioInitializationCoordinator.Initialize(
            true, factory.Succeed, _ => true);

        Assert.Equal(AudioInitializationMode.Classic, result);
        Assert.Equal([AudioInitializationMode.Hybrid, AudioInitializationMode.Classic], factory.Attempts);
        Assert.Equal(2, factory.ReleaseCount);
    }

    [Fact]
    public void PartialHybridRenderFailureReleasesPairBeforeFreshClassicSuccess()
    {
        var factory = new RecordingAttemptFactory
        {
            HybridFailure = CreateComException(unchecked((int)0x80004005))
        };

        var result = AudioInitializationCoordinator.Initialize(
            true, factory.Succeed, WasapiAudioMonitorSession.IsHybridFallbackEligible);

        Assert.Equal(AudioInitializationMode.Classic, result);
        Assert.Equal(2, factory.ReleaseCount);
        Assert.Equal(2, factory.ActiveOwnershipCount);
        Assert.Equal(2, factory.Attempts.Count);
    }

    [Fact]
    public void DeviceInvalidationDoesNotTriggerMisleadingFormatFallback()
    {
        var invalidated = CreateComException(unchecked((int)0x88890004));
        var factory = new RecordingAttemptFactory { HybridFailure = invalidated };

        var observed = Assert.Throws<COMException>(() => AudioInitializationCoordinator.Initialize(
            true, factory.Succeed, WasapiAudioMonitorSession.IsHybridFallbackEligible));

        Assert.Same(invalidated, observed);
        Assert.Equal([AudioInitializationMode.Hybrid], factory.Attempts);
        Assert.Equal(2, factory.ReleaseCount);
    }

    [Fact]
    public void AccessDeniedAndAudioServiceFailureRemainPrecise()
    {
        Assert.False(WasapiAudioMonitorSession.IsHybridFallbackEligibleHResult(unchecked((int)0x80070005)));
        Assert.False(WasapiAudioMonitorSession.IsHybridFallbackEligibleHResult(unchecked((int)0x88890010)));
        Assert.True(WasapiAudioMonitorSession.IsHybridFallbackEligibleHResult(unchecked((int)0x80004005)));
    }

    [Fact]
    public void BothAttemptsFailAndEveryPartialOwnershipIsReleased()
    {
        var factory = new RecordingAttemptFactory
        {
            HybridFailure = new AudioInitializationFallbackException("hybrid"),
            ClassicFailure = CreateComException(unchecked((int)0x88890008))
        };

        Assert.Throws<COMException>(() => AudioInitializationCoordinator.Initialize(
            true, factory.Succeed, _ => true));

        Assert.Equal(4, factory.ReleaseCount);
        Assert.Equal(0, factory.ActiveOwnershipCount);
        Assert.Equal([AudioInitializationMode.Hybrid, AudioInitializationMode.Classic], factory.Attempts);
    }

    private sealed class RecordingAttemptFactory
    {
        internal List<AudioInitializationMode> Attempts { get; } = [];
        internal Exception? HybridFailure { get; init; }
        internal Exception? ClassicFailure { get; init; }
        internal int ReleaseCount { get; private set; }
        internal int ActiveOwnershipCount { get; private set; }

        internal AudioInitializationMode Succeed(AudioInitializationMode mode)
        {
            Attempts.Add(mode);
            ActiveOwnershipCount += 2;
            var failure = mode == AudioInitializationMode.Hybrid ? HybridFailure : ClassicFailure;
            if (failure is null) return mode;
            ReleaseCount += 2;
            ActiveOwnershipCount -= 2;
            throw failure;
        }
    }

    private static COMException CreateComException(int hresult)
    {
        try { Marshal.ThrowExceptionForHR(hresult); }
        catch (COMException exception) { return exception; }
        throw new InvalidOperationException("The supplied HRESULT did not create a COM exception.");
    }
}

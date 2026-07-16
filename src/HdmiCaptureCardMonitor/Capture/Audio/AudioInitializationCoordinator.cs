namespace HdmiCaptureCardMonitor.Capture.Audio;

internal enum AudioInitializationMode
{
    Hybrid,
    Classic
}

internal static class AudioInitializationCoordinator
{
    internal static T Initialize<T>(
        bool preferHybrid,
        Func<AudioInitializationMode, T> createFreshAttempt,
        Func<Exception, bool> isFallbackEligible,
        Action<Exception>? fallbackObserved = null)
    {
        ArgumentNullException.ThrowIfNull(createFreshAttempt);
        ArgumentNullException.ThrowIfNull(isFallbackEligible);
        if (!preferHybrid) return createFreshAttempt(AudioInitializationMode.Classic);

        try
        {
            return createFreshAttempt(AudioInitializationMode.Hybrid);
        }
        catch (Exception exception) when (isFallbackEligible(exception))
        {
            fallbackObserved?.Invoke(exception);
            return createFreshAttempt(AudioInitializationMode.Classic);
        }
    }
}

internal sealed class AudioInitializationFallbackException(string message) : Exception(message);

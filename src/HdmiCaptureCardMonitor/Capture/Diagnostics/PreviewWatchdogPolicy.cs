using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Diagnostics;

internal static class PreviewWatchdogPolicy
{
    public static PreviewFailureCategory? Evaluate(
        TimeSpan sessionAge,
        TimeSpan? timeSinceSample,
        TimeSpan? timeSincePresentation,
        bool surfacePresentable,
        bool firstFramePresented,
        TimeSpan threshold)
    {
        var sampleAge = timeSinceSample ?? sessionAge;
        if (sampleAge > threshold)
        {
            return firstFramePresented
                ? PreviewFailureCategory.PreviewStalled
                : PreviewFailureCategory.StartupTimeout;
        }

        if (!surfacePresentable) return null;

        var presentationAge = timeSincePresentation ?? sessionAge;
        return presentationAge > threshold ? PreviewFailureCategory.PresentationFailure : null;
    }
}

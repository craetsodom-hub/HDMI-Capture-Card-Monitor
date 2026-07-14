using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Interop;

public static class MediaFoundationStartupClassifier
{
    public static MediaFoundationStartupStatus Classify(int hresult) => hresult switch
    {
        MediaFoundationHResults.ENotImplemented => MediaFoundationStartupStatus.MissingMediaComponents,
        MediaFoundationHResults.BadStartupVersion => MediaFoundationStartupStatus.UnsupportedStartupVersion,
        MediaFoundationHResults.DisabledInSafeMode => MediaFoundationStartupStatus.DisabledInSafeMode,
        _ => MediaFoundationStartupStatus.OtherFailure
    };
}

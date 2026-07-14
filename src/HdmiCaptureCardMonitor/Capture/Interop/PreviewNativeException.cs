using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed class PreviewNativeException : Exception
{
    public PreviewNativeException(PreviewFailure failure)
        : base(failure.SafeMessage, failure.Exception)
    {
        Failure = failure;
    }

    public PreviewFailure Failure { get; }
}

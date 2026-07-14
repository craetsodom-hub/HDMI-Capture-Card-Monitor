using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Interop;

internal static class PreviewFailureMapper
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int CodecNotFound = unchecked((int)0xC00D5212);
    private const int InvalidMediaType = unchecked((int)0xC00D36B4);

    public static PreviewFailureCategory Map(int hresult, PreviewFailureCategory fallback = PreviewFailureCategory.Unknown) => hresult switch
    {
        AccessDenied => PreviewFailureCategory.AccessDenied,
        SharingViolation => PreviewFailureCategory.DeviceBusy,
        DeviceRemoved or DeviceReset => PreviewFailureCategory.DeviceRemoved,
        CodecNotFound => PreviewFailureCategory.DecoderUnavailable,
        InvalidMediaType => PreviewFailureCategory.UnsupportedPreviewFormat,
        _ => fallback
    };
}

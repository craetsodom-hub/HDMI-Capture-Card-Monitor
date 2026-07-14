namespace HdmiCaptureCardMonitor.Models;

public enum PreviewFailureCategory
{
    Unknown,
    AccessDenied,
    DeviceBusy,
    DeviceUnavailable,
    SelectedFormatUnavailable,
    DecoderUnavailable,
    UnsupportedPreviewFormat,
    D3DInitializationFailure,
    DeviceRemoved,
    UnsupportedGpuBuffer,
    PresentationFailure,
    MediaTypeChanged,
    EndOfStream,
    PreviewStalled,
    StartupTimeout,
    ShutdownTimeout
}

namespace HdmiCaptureCardMonitor.Models;

public enum DiscoveryFailureCategory
{
    MissingMediaComponents,
    AccessDenied,
    DeviceUnavailable,
    DeviceBusy,
    NoUsableFormats,
    InvalidNativeData,
    ComApartmentFailure,
    Cancelled,
    Unknown
}

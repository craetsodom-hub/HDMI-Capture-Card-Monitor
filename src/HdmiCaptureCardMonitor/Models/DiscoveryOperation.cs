namespace HdmiCaptureCardMonitor.Models;

public enum DiscoveryOperation
{
    MediaFoundationInitialization,
    DeviceEnumeration,
    SelectedDeviceActivation,
    NativeMediaTypeDiscovery,
    Cleanup,
    CleanupShutdown,
    Shutdown
}

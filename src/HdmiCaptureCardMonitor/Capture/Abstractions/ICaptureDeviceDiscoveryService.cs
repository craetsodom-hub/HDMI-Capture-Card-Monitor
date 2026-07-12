using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Abstractions;

public interface ICaptureDeviceDiscoveryService : IDisposable
{
    Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken);

    Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(
        CaptureDevice device,
        CancellationToken cancellationToken);
}

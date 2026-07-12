using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Devices;

public sealed class UnavailableCaptureDeviceDiscoveryService : ICaptureDeviceDiscoveryService
{
    private readonly DiscoveryFailure failure;

    public UnavailableCaptureDeviceDiscoveryService(string message) =>
        failure = new DiscoveryFailure(DiscoveryOperation.MediaFoundationInitialization, null, message);

    public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(failure));

    public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) =>
        Task.FromResult(DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(failure));

    public void Dispose() { }
}

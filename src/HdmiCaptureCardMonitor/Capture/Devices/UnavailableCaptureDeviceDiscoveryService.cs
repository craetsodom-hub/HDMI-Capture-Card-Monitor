using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Devices;

public sealed class UnavailableCaptureDeviceDiscoveryService : ICaptureDeviceDiscoveryService
{
    private readonly DiscoveryFailure failure;

    public UnavailableCaptureDeviceDiscoveryService(string message, DiscoveryFailureCategory category = DiscoveryFailureCategory.Unknown, int? hresult = null) =>
        failure = new DiscoveryFailure(DiscoveryOperation.MediaFoundationInitialization, category, hresult, message);

    public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(failure));

    public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) =>
        Task.FromResult(DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(failure));

    public void Dispose() { }
}

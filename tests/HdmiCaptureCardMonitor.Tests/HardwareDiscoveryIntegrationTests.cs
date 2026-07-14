using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Infrastructure;
using Xunit.Abstractions;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class HardwareDiscoveryIntegrationTests
{
    private readonly ITestOutputHelper output;

    public HardwareDiscoveryIntegrationTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task EnumeratesConnectedVideoDevicesAndReleasesThemAfterFormatDiscovery()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HDMI_CAPTURE_HARDWARE_VALIDATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var runtime = new MediaFoundationRuntime();
        Assert.True(runtime.Initialize().IsSuccess);
        using var service = new MediaFoundationDeviceDiscoveryService(NullApplicationLogger.Instance);

        var firstEnumeration = await service.EnumerateVideoDevicesAsync(CancellationToken.None);
        Assert.True(firstEnumeration.IsSuccess, firstEnumeration.Failure?.TechnicalMessage);
        Assert.NotEmpty(firstEnumeration.Value!);
        var device = firstEnumeration.Value![0];

        var firstFormats = await service.GetNativeVideoCapabilitiesAsync(device, CancellationToken.None);
        Assert.True(firstFormats.IsSuccess, firstFormats.Failure?.TechnicalMessage);
        Assert.NotEmpty(firstFormats.Value!);
        output.WriteLine($"Device: {device.DisplayName}; native formats: {firstFormats.Value!.Count}");
        foreach (var format in firstFormats.Value!.Take(6)) output.WriteLine(format.DisplayLabel);

        // Reopening the same device confirms the first source reader/media source was released.
        var secondFormats = await service.GetNativeVideoCapabilitiesAsync(device, CancellationToken.None);
        Assert.True(secondFormats.IsSuccess, secondFormats.Failure?.TechnicalMessage);
        Assert.NotEmpty(secondFormats.Value!);

        var refresh = await service.EnumerateVideoDevicesAsync(CancellationToken.None);
        Assert.True(refresh.IsSuccess, refresh.Failure?.TechnicalMessage);

        using var closingService = new MediaFoundationDeviceDiscoveryService(NullApplicationLogger.Instance);
        var inFlightDiscovery = closingService.EnumerateVideoDevicesAsync(CancellationToken.None);
        closingService.Dispose();
        var closingResult = await inFlightDiscovery;
        Assert.True(closingResult.IsSuccess || closingResult.IsCancelled, closingResult.Failure?.TechnicalMessage);
    }
}

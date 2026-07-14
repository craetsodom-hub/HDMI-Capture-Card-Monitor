using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.Capture.Interop;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseOneManagedBehaviorTests
{
    [Theory]
    [InlineData(30, 1, "30 fps")]
    [InlineData(60, 1, "60 fps")]
    [InlineData(30000, 1001, "29.97 fps")]
    [InlineData(60000, 1001, "59.94 fps")]
    [InlineData(1, 0, "Unknown fps")]
    public void FormatsFrameRatesWithoutLosingRationalPrecision(uint numerator, uint denominator, string expected) =>
        Assert.Equal(expected, NativeVideoCapabilityFormatter.FormatFrameRate(numerator, denominator));

    [Fact]
    public void NormalizesDuplicateFriendlyNamesWithoutExposingOpaqueIds()
    {
        var devices = CaptureDeviceNormalizer.Normalize(
        [
            new CaptureDevice("opaque-one", "USB Video", string.Empty, null, CaptureBackend.MediaFoundation),
            new CaptureDevice("opaque-two", "USB Video", string.Empty, null, CaptureBackend.MediaFoundation),
            new CaptureDevice("opaque-three", "", string.Empty, null, CaptureBackend.MediaFoundation)
        ]);

        Assert.Equal(["Unnamed video device", "USB Video (1)", "USB Video (2)"], devices.Select(device => device.DisplayName));
        Assert.DoesNotContain("opaque", string.Join(' ', devices.Select(device => device.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public void SortsAndDeduplicatesOnlyExactNativeCapabilities()
    {
        var subtype = Guid.NewGuid();
        var sixty = CreateCapability(60, 1, "YUY2", 1, subtype);
        var fiftyNinePointNineFour = CreateCapability(60000, 1001, "YUY2", 2, subtype);
        var duplicateSixty = CreateCapability(60, 1, "YUY2", 1, subtype);

        var result = NativeVideoCapabilityFormatter.SortAndDeduplicate([fiftyNinePointNineFour, duplicateSixty, sixty]);

        Assert.Equal(2, result.Count);
        Assert.Equal(60d, result[0].ExactFrameRate);
        Assert.Equal(60000d / 1001d, result[1].ExactFrameRate);
    }

    [Theory]
    [InlineData(VideoInterlaceMode.Progressive, "1920 × 1080p · 60 fps · MJPEG")]
    [InlineData(VideoInterlaceMode.Interlaced, "1920 × 1080i · 60 fps · MJPEG")]
    [InlineData(VideoInterlaceMode.Mixed, "1920 × 1080 · 60 fps · MJPEG · Mixed scan")]
    [InlineData(VideoInterlaceMode.Unknown, "1920 × 1080 · 60 fps · MJPEG · Scan unknown")]
    public void FormatsScanModesHonestly(VideoInterlaceMode mode, string expected) =>
        Assert.Equal(expected, NativeVideoCapabilityFormatter.CreateDisplayLabel(1920, 1080, 60, 1, "MJPEG", mode));

    [Fact]
    public void DeduplicationKeepsLowestIndexButPreservesPixelAspectRatioDifferences()
    {
        var subtype = Guid.NewGuid();
        var highIndex = CreateCapability(60, 1, "YUY2", 4, subtype) with { PixelAspectRatioNumerator = 1, PixelAspectRatioDenominator = 1 };
        var lowIndex = CreateCapability(60, 1, "YUY2", 1, subtype) with { PixelAspectRatioNumerator = 1, PixelAspectRatioDenominator = 1 };
        var differentAspect = CreateCapability(60, 1, "YUY2", 2, subtype) with { PixelAspectRatioNumerator = 4, PixelAspectRatioDenominator = 3 };

        var result = NativeVideoCapabilityFormatter.SortAndDeduplicate([highIndex, differentAspect, lowIndex]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, capability => capability.NativeMediaTypeIndex == 1);
        Assert.Contains(result, capability => capability.PixelAspectRatioNumerator == 4);
    }

    [Theory]
    [InlineData(-2147467263, DiscoveryFailureCategory.MissingMediaComponents)]
    [InlineData(-2147024891, DiscoveryFailureCategory.AccessDenied)]
    [InlineData(-2147467259, DiscoveryFailureCategory.Unknown)]
    public void FailureCategoriesAreSafeManagedValues(int hresult, DiscoveryFailureCategory category)
    {
        var failure = new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, category, hresult, "safe message");
        Assert.Equal(category, failure.Category);
        Assert.Equal($"0x{hresult:X8}", failure.HResultDisplay);
    }

    [Theory]
    [InlineData(2, VideoInterlaceMode.Progressive)]
    [InlineData(3, VideoInterlaceMode.Interlaced)]
    [InlineData(4, VideoInterlaceMode.Interlaced)]
    [InlineData(5, VideoInterlaceMode.Interlaced)]
    [InlineData(6, VideoInterlaceMode.Interlaced)]
    [InlineData(7, VideoInterlaceMode.Mixed)]
    [InlineData(0, VideoInterlaceMode.Unknown)]
    [InlineData(99, VideoInterlaceMode.Unknown)]
    public void MapsMediaFoundationInterlaceValuesExactly(uint nativeValue, VideoInterlaceMode expected) =>
        Assert.Equal(expected, MediaFoundationDeviceDiscoveryService.MapInterlaceMode(nativeValue));

    [Fact]
    public void OnlyMfShutdownIsRecognizedAsAlreadyShutDownCleanup()
    {
        Assert.True(MediaFoundationDeviceDiscoveryService.IsAlreadyShutdownResult(MediaFoundationHResults.Shutdown));
        Assert.False(MediaFoundationDeviceDiscoveryService.IsAlreadyShutdownResult(unchecked((int)0x80004005)));
    }

    [Theory]
    [InlineData(DiscoveryOperation.SelectedDeviceActivation)]
    [InlineData(DiscoveryOperation.NativeMediaTypeDiscovery)]
    [InlineData(DiscoveryOperation.CleanupShutdown)]
    public void CapabilityFailureHelperPreservesOperation(DiscoveryOperation operation)
    {
        var result = MediaFoundationDeviceDiscoveryService.FailedCapabilities(operation, DiscoveryFailureCategory.Unknown, unchecked((int)0x80004005), "safe");
        Assert.Equal(operation, result.Failure!.Operation);
    }

    private static NativeVideoCapability CreateCapability(uint numerator, uint denominator, string subtype, int index, Guid subtypeGuid) =>
        new(0, index, 1920, 1080, numerator, denominator, NativeVideoCapabilityFormatter.CalculateFrameRate(numerator, denominator), subtypeGuid, subtype, VideoInterlaceMode.Progressive, 1, 1, "test");
}

using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Models;

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

    private static NativeVideoCapability CreateCapability(uint numerator, uint denominator, string subtype, int index, Guid subtypeGuid) =>
        new(0, index, 1920, 1080, numerator, denominator, NativeVideoCapabilityFormatter.CalculateFrameRate(numerator, denominator), subtypeGuid, subtype, VideoInterlaceMode.Progressive, 1, 1, "test");
}

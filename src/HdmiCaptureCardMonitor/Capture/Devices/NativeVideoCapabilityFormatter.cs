using System.Globalization;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Devices;

public static class NativeVideoCapabilityFormatter
{
    public static double CalculateFrameRate(uint numerator, uint denominator) => denominator == 0 ? 0d : (double)numerator / denominator;

    public static string FormatFrameRate(uint numerator, uint denominator)
    {
        var rate = CalculateFrameRate(numerator, denominator);
        return rate == 0d ? "Unknown fps" : $"{rate.ToString(rate % 1d == 0d ? "0" : "0.##", CultureInfo.InvariantCulture)} fps";
    }

    public static string CreateDisplayLabel(uint width, uint height, uint numerator, uint denominator, string subtype, VideoInterlaceMode interlace)
    {
        var scan = interlace == VideoInterlaceMode.Interlaced ? "i" : string.Empty;
        return $"{width} × {height}{scan} · {FormatFrameRate(numerator, denominator)} · {subtype}";
    }

    public static IReadOnlyList<NativeVideoCapability> SortAndDeduplicate(IEnumerable<NativeVideoCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capabilities
            .DistinctBy(capability => new
            {
                capability.StreamIndex,
                capability.NativeMediaTypeIndex,
                capability.Width,
                capability.Height,
                capability.FrameRateNumerator,
                capability.FrameRateDenominator,
                capability.MediaSubtype,
                capability.InterlaceMode
            })
            .OrderByDescending(capability => (ulong)capability.Width * capability.Height)
            .ThenByDescending(capability => capability.ExactFrameRate)
            .ThenBy(capability => capability.InterlaceMode == VideoInterlaceMode.Progressive ? 0 : 1)
            .ThenBy(capability => capability.SubtypeLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(capability => capability.NativeMediaTypeIndex)
            .ToList();
    }
}

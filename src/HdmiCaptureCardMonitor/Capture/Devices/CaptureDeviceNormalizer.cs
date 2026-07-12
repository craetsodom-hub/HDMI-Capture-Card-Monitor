using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Devices;

public static class CaptureDeviceNormalizer
{
    public static IReadOnlyList<CaptureDevice> Normalize(IEnumerable<CaptureDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var ordered = devices
            .Select(device => device with { FriendlyName = NormalizeFriendlyName(device.FriendlyName) })
            .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var group in ordered.GroupBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            var index = 1;
            foreach (var device in group)
            {
                var displayName = group.Count() == 1 ? device.FriendlyName : $"{device.FriendlyName} ({index++})";
                var position = ordered.IndexOf(device);
                ordered[position] = device with { DisplayName = displayName };
            }
        }

        return ordered;
    }

    private static string NormalizeFriendlyName(string? friendlyName) =>
        string.IsNullOrWhiteSpace(friendlyName) ? "Unnamed video device" : friendlyName.Trim();
}

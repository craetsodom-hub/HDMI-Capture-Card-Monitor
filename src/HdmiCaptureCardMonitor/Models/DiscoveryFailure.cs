namespace HdmiCaptureCardMonitor.Models;

public sealed record DiscoveryFailure(
    DiscoveryOperation Operation,
    int? HResult,
    string TechnicalMessage,
    Exception? InnerException = null)
{
    public string HResultDisplay => HResult is null ? "Unavailable" : $"0x{HResult.Value:X8}";
}

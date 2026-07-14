namespace HdmiCaptureCardMonitor.Models;

/// <summary>Managed description of a Windows video-capture input.</summary>
public sealed record CaptureDevice(
    string Id,
    string FriendlyName,
    string DisplayName,
    bool? IsHardwareSource,
    CaptureBackend Backend)
{
    public override string ToString() => DisplayName;
}

using HdmiCaptureCardMonitor.Capture.Audio;

namespace HdmiCaptureCardMonitor.Presentation;

public sealed record AudioEndpointChoice(string DisplayName, AudioEndpoint? Endpoint, bool IsSystemDefaultOutput = false)
{
    public static AudioEndpointChoice NoAudio { get; } = new("No audio", null);
    public static AudioEndpointChoice SystemDefaultOutput { get; } = new("System default output", null, true);

    public override string ToString() => DisplayName;
}

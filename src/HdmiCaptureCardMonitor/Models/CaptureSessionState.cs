namespace HdmiCaptureCardMonitor.Models;

/// <summary>Represents the high-level lifecycle of a future capture session.</summary>
public enum CaptureSessionState
{
    Idle, Enumerating, DeviceReady, Starting, Previewing, Recording, Reconnecting, Stopping, Faulted
}

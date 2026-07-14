namespace HdmiCaptureCardMonitor.Capture.Interop;

/// <summary>HRESULT values from the installed Windows SDK headers that CsWin32 does not emit as managed constants.</summary>
internal static class MediaFoundationHResults
{
    public const int ENotImplemented = unchecked((int)0x80004001);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);
    public const int BadStartupVersion = unchecked((int)0xC00D36E3);
    public const int DisabledInSafeMode = unchecked((int)0xC00D36EF);
    public const int Shutdown = unchecked((int)0xC00D3E85);
}

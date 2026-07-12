namespace HdmiCaptureCardMonitor.Infrastructure;

/// <summary>Local no-op logger used only when local file logging cannot be created.</summary>
public sealed class NullApplicationLogger : IApplicationLogger
{
    public static NullApplicationLogger Instance { get; } = new();

    private NullApplicationLogger()
    {
    }

    public void Debug(string message)
    {
    }

    public void Information(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void LogError(string message, Exception? exception = null)
    {
    }
}

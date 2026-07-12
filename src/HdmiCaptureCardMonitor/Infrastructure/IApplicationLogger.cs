namespace HdmiCaptureCardMonitor.Infrastructure;

public interface IApplicationLogger
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

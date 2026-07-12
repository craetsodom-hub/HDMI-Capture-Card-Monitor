using System.Globalization;
using System.IO;
using System.Text;

namespace HdmiCaptureCardMonitor.Infrastructure;

/// <summary>Minimal local-only logger. Callers must not pass captured media or sensitive user data.</summary>
public sealed class LocalFileApplicationLogger : IApplicationLogger, IDisposable
{
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;
    private bool disposed;

    public LocalFileApplicationLogger()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HdmiCaptureCardMonitor", "Logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"app-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Information(string message) => Write(LogLevel.Information, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            writer.Dispose();
            disposed = true;
        }
    }

    private void Write(LogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} [{level}] {message}"));
        }
    }
}

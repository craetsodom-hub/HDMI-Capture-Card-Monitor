using System.Globalization;
using System.IO;
using System.Text;

namespace HdmiCaptureCardMonitor.Infrastructure;

/// <summary>Writes safe diagnostic text to a bounded set of local application log files.</summary>
public sealed class LocalFileApplicationLogger : IApplicationLogger, IDisposable
{
    public const int DefaultRetentionCount = 10;

    private readonly object syncRoot = new();
    private readonly StreamWriter writer;
    private bool disposed;

    public LocalFileApplicationLogger(
        string directory,
        int retentionCount = DefaultRetentionCount,
        Func<DateTimeOffset>? utcNow = null,
        Func<int>? processId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionCount);

        Directory.CreateDirectory(directory);
        writer = CreateWriter(directory, utcNow?.Invoke() ?? DateTimeOffset.UtcNow, processId?.Invoke() ?? Environment.ProcessId);
        ApplyRetention(directory, retentionCount);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Information(string message) => Write(LogLevel.Information, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void LogError(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            writer.Dispose();
            disposed = true;
        }
    }

    private static StreamWriter CreateWriter(string directory, DateTimeOffset timestamp, int processId)
    {
        var fileStem = string.Create(
            CultureInfo.InvariantCulture,
            $"app-{timestamp.UtcDateTime:yyyyMMdd-HHmmss-fffffff}-{processId}");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var path = Path.Combine(directory, $"{fileStem}{suffix}.log");

            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent instance used this name. Try a deterministic suffix.
            }
        }

        throw new IOException("Could not create a unique application log file.");
    }

    private static void ApplyRetention(string directory, int retentionCount)
    {
        try
        {
            var oldLogs = Directory.EnumerateFiles(directory, "app-*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(retentionCount);

            foreach (var oldLog in oldLogs)
            {
                oldLog.Delete();
            }
        }
        catch (IOException)
        {
            // Retention must never prevent startup; unrelated files are never targeted.
        }
        catch (UnauthorizedAccessException)
        {
            // Retention must never prevent startup; unrelated files are never targeted.
        }
    }

    private void Write(LogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} [{level}] {message}"));
            }
            catch (IOException)
            {
                // Logging must not destabilize the monitor shell when the local disk becomes unavailable.
            }
        }
    }
}

using System.IO;

namespace HdmiCaptureCardMonitor.Infrastructure;

/// <summary>Creates the application logger and reports a non-fatal local logging failure to the shell.</summary>
public static class ApplicationLoggerFactory
{
    public static ApplicationLoggerCreationResult CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HdmiCaptureCardMonitor",
            "Logs");

        return Create(directory);
    }

    public static ApplicationLoggerCreationResult Create(string directory)
    {
        try
        {
            return new ApplicationLoggerCreationResult(new LocalFileApplicationLogger(directory), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ApplicationLoggerCreationResult(
                NullApplicationLogger.Instance,
                "Local logging is unavailable. The application will continue without file logs.");
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Windows;
using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor;

public partial class App : Application, IDisposable
{
    private IApplicationLogger? logger;
    private MediaFoundationRuntime? mediaFoundationRuntime;
    private MediaFoundationDeviceDiscoveryService? discoveryService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var loggerCreation = ApplicationLoggerFactory.CreateDefault();
        logger = loggerCreation.Logger;
        logger.Information("Application startup completed.");
        mediaFoundationRuntime = new MediaFoundationRuntime();
        discoveryService = new MediaFoundationDeviceDiscoveryService(mediaFoundationRuntime);
        new MainWindow(logger, discoveryService, loggerCreation.StartupNotice).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownLogger(logger);
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        discoveryService?.Dispose();
        mediaFoundationRuntime?.Dispose();
        discoveryService = null;
        mediaFoundationRuntime = null;
        GC.SuppressFinalize(this);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An external logger must never prevent WPF application shutdown.")]
    private static void ShutdownLogger(IApplicationLogger? applicationLogger)
    {
        try
        {
            applicationLogger?.Information("Application shutdown completed.");
        }
        catch (Exception)
        {
            // Shutdown must continue even if a logger implementation is faulty.
        }

        try
        {
            (applicationLogger as IDisposable)?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown must continue even if a disposable logger is faulty.
        }
    }
}

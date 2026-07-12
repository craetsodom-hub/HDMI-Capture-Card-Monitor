using System.Windows;
using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor;

public partial class App : Application
{
    private LocalFileApplicationLogger? logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        logger = new LocalFileApplicationLogger();
        logger.Information("Application startup completed.");
        new MainWindow(logger).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger?.Information("Application shutdown completed.");
        logger?.Dispose();
        base.OnExit(e);
    }
}

using System.Windows;
using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor;

public partial class App : Application
{
    private IApplicationLogger? logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var loggerCreation = ApplicationLoggerFactory.CreateDefault();
        logger = loggerCreation.Logger;
        logger.Information("Application startup completed.");
        new MainWindow(logger, loggerCreation.StartupNotice).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger?.Information("Application shutdown completed.");
        (logger as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}

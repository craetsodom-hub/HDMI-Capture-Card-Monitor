using System.Diagnostics.CodeAnalysis;
using System.Windows;
using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor;

public partial class App : Application, IDisposable
{
    private IApplicationLogger? logger;
    private MediaFoundationRuntime? mediaFoundationRuntime;
    private ICaptureDeviceDiscoveryService? discoveryService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var loggerCreation = ApplicationLoggerFactory.CreateDefault();
        logger = loggerCreation.Logger;
        mediaFoundationRuntime = new MediaFoundationRuntime();
        var startup = mediaFoundationRuntime.Initialize();
        logger.Information($"Media Foundation startup result: {startup.Status} ({FormatHResult(startup.HResult)}).");
        string? startupNotice = loggerCreation.StartupNotice;
        if (startup.IsSuccess)
        {
            discoveryService = new MediaFoundationDeviceDiscoveryService(logger);
        }
        else
        {
            var category = startup.Status == MediaFoundationStartupStatus.MissingMediaComponents ? DiscoveryFailureCategory.MissingMediaComponents : DiscoveryFailureCategory.Unknown;
            startupNotice = startup.Status == MediaFoundationStartupStatus.MissingMediaComponents
                ? "Required Windows media components are unavailable. Install the Media Feature Pack from Windows Optional Features, restart Windows, and select Refresh."
                : "Video discovery is unavailable on this Windows installation. Select Refresh after resolving the Windows media components issue.";
            discoveryService = new UnavailableCaptureDeviceDiscoveryService("Media Foundation startup failed.", category, startup.HResult);
        }
        logger.Information("Application startup completed.");
        new MainWindow(logger, discoveryService, startupNotice).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        ShutdownLogger(logger);
        base.OnExit(e);
    }

    public void Dispose()
    {
        discoveryService?.Dispose();
        if (discoveryService is not MediaFoundationDeviceDiscoveryService activeService || activeService.WorkersSettled)
        {
            mediaFoundationRuntime?.Dispose();
        }
        else
        {
            logger?.Warning("Media Foundation shutdown was skipped because a discovery worker exceeded the bounded shutdown period.");
        }
        discoveryService = null;
        mediaFoundationRuntime = null;
        GC.SuppressFinalize(this);
    }

    private static string FormatHResult(int? hresult) => hresult is null ? "not attempted" : $"0x{hresult.Value:X8}";

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

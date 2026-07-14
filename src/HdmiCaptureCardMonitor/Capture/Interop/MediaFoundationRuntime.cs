using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using Windows.Win32;

namespace HdmiCaptureCardMonitor.Capture.Interop;

/// <summary>Composition-root owner of the single process-wide Media Foundation lifetime.</summary>
public sealed class MediaFoundationRuntime : IDisposable
{
    private static readonly MediaFoundationStartupResult DisposedResult = new(MediaFoundationStartupStatus.OtherFailure, null);
    private readonly object syncRoot = new();
    private readonly Func<int> startup;
    private readonly Func<int> shutdown;
    private readonly IApplicationLogger logger;
    private MediaFoundationStartupResult? cachedResult;
    private bool started;
    private bool disposed;

    public MediaFoundationRuntime(IApplicationLogger? logger = null)
        : this(
            () => PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).Value,
            () => PInvoke.MFShutdown().Value,
            logger ?? NullApplicationLogger.Instance)
    {
    }

    internal MediaFoundationRuntime(Func<int> startup, Func<int> shutdown, IApplicationLogger logger)
    {
        this.startup = startup;
        this.shutdown = shutdown;
        this.logger = logger;
    }

    public MediaFoundationStartupResult Initialize()
    {
        lock (syncRoot)
        {
            if (disposed) return DisposedResult;
            if (cachedResult is not null) return cachedResult;

            var hresult = startup();
            started = hresult >= 0;
            cachedResult = started
                ? new MediaFoundationStartupResult(MediaFoundationStartupStatus.Success, hresult)
                : new MediaFoundationStartupResult(MediaFoundationStartupClassifier.Classify(hresult), hresult);
            return cachedResult;
        }
    }

    public bool IsStarted { get { lock (syncRoot) return started; } }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
            if (!started) return;

            var hresult = shutdown();
            if (hresult < 0) logger.Warning($"Media Foundation shutdown reported 0x{hresult:X8}.");
            started = false;
        }
    }
}

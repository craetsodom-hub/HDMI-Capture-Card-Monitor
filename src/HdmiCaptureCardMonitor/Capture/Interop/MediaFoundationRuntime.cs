using HdmiCaptureCardMonitor.Models;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HdmiCaptureCardMonitor.Capture.Interop;

/// <summary>Composition-root owner of the single process-wide Media Foundation lifetime.</summary>
public sealed class MediaFoundationRuntime : IDisposable
{
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int MfENotInitialized = unchecked((int)0xC00D36B0);
    private readonly object syncRoot = new();
    private bool initializeAttempted;
    private bool started;
    private bool disposed;

    public MediaFoundationStartupResult Initialize()
    {
        lock (syncRoot)
        {
            if (initializeAttempted)
            {
                return new(started ? MediaFoundationStartupStatus.Success : MediaFoundationStartupStatus.OtherFailure, null);
            }

            initializeAttempted = true;
            var result = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL);
            if (!result.Failed)
            {
                started = true;
                return new(MediaFoundationStartupStatus.Success, result.Value);
            }

            return new(MapStatus(result.Value), result.Value);
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
            PInvoke.MFShutdown();
            started = false;
        }
    }

    private static MediaFoundationStartupStatus MapStatus(int hresult) => hresult switch
    {
        ENotImpl => MediaFoundationStartupStatus.MissingMediaComponents,
        MfENotInitialized => MediaFoundationStartupStatus.UnsupportedStartupVersion,
        _ => MediaFoundationStartupStatus.OtherFailure
    };
}

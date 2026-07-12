using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.Media.MediaFoundation;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Interop;

/// <summary>Owns the process-wide Media Foundation startup/shutdown pair for discovery operations.</summary>
public sealed class MediaFoundationRuntime : IDisposable
{
    private readonly object syncRoot = new();
    private bool started;
    private bool disposed;

    public DiscoveryResult<bool> EnsureStarted()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return DiscoveryResults.Failed<bool>(new DiscoveryFailure(DiscoveryOperation.MediaFoundationInitialization, null, "The Media Foundation runtime has already been shut down."));
            }

            if (started)
            {
                return DiscoveryResults.Success(true);
            }

            var version = (uint)((PInvoke.MF_SDK_VERSION << 16) | PInvoke.MF_API_VERSION);
            var result = PInvoke.MFStartup(version, PInvoke.MFSTARTUP_FULL);
            if (result.Failed)
            {
                return DiscoveryResults.Failed<bool>(new DiscoveryFailure(DiscoveryOperation.MediaFoundationInitialization, result.Value, "Media Foundation startup failed."));
            }

            started = true;
            return DiscoveryResults.Success(true);
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (started)
            {
                PInvoke.MFShutdown();
                started = false;
            }
        }
    }
}

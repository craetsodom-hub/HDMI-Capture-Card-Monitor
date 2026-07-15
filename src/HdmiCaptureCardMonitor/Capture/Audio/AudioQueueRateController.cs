using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Infrastructure;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal static class AudioQueueRateControllerMath
{
    internal const double MaximumAdjustmentPpm = 3_000;
    private const double AdjustmentPpmPerPeriod = 2_000;

    internal static double CalculateAdjustmentPpm(int queueFrames, int targetQueueFrames, int periodFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queueFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(targetQueueFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodFrames);
        var errorPeriods = (queueFrames - targetQueueFrames) / (double)periodFrames;
        return Math.Clamp(errorPeriods * AdjustmentPpmPerPeriod, -MaximumAdjustmentPpm, MaximumAdjustmentPpm);
    }
}

/// <summary>
/// Applies a small queue-error correction on a non-real-time MTA thread. The
/// MMCSS packet worker publishes only the current queue depth and never calls
/// IAudioClockAdjustment.SetSampleRate.
/// </summary>
internal sealed class AudioQueueRateController : IDisposable
{
    private const int ControlIntervalMilliseconds = 500;
    private const int StartupTimeoutMilliseconds = 2_000;
    private const int StopTimeoutMilliseconds = 2_000;
    private const double MinimumChangePpm = 50;

    private readonly IAudioClient renderClient;
    private readonly IApplicationLogger logger;
    private readonly double nominalSampleRate;
    private readonly int targetQueueFrames;
    private readonly int periodFrames;
    private readonly ManualResetEvent stopEvent = new(false);
    private readonly ManualResetEvent initializedEvent = new(false);
    private readonly Thread thread;
    private int queueFrames;
    private long appliedPpmBits;
    private int available;
    private int disposed;

    internal AudioQueueRateController(
        IAudioClient renderClient,
        IApplicationLogger logger,
        int nominalSampleRate,
        int targetQueueFrames,
        int periodFrames)
    {
        this.renderClient = renderClient;
        this.logger = logger;
        this.nominalSampleRate = nominalSampleRate;
        this.targetQueueFrames = targetQueueFrames;
        this.periodFrames = periodFrames;
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Audio queue rate controller",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal double? AppliedAdjustmentPpm => Volatile.Read(ref available) != 0
        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref appliedPpmBits))
        : null;

    internal bool IsAvailable => Volatile.Read(ref available) != 0;

    internal void UpdateQueueDepth(int value) => Volatile.Write(ref queueFrames, value);

    internal bool Start()
    {
        thread.Start();
        return initializedEvent.WaitOne(StartupTimeoutMilliseconds) && Volatile.Read(ref available) != 0;
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return !thread.IsAlive;
        stopEvent.Set();
        if (thread.IsAlive && !thread.Join(StopTimeoutMilliseconds))
        {
            SafeWarning("The audio queue-rate controller did not stop within its two-second bound.");
            return false;
        }
        initializedEvent.Dispose();
        stopEvent.Dispose();
        return true;
    }

    public void Dispose() => _ = Stop();

    private unsafe void ThreadMain()
    {
        IAudioClockAdjustment? adjustment = null;
        var comInitialized = false;
        try
        {
            var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartment.Failed) Marshal.ThrowExceptionForHR(apartment.Value);
            comInitialized = true;
            renderClient.GetService(out adjustment);
            var activeAdjustment = adjustment!;
            Volatile.Write(ref available, 1);
            initializedEvent.Set();

            var appliedPpm = 0d;
            while (!stopEvent.WaitOne(ControlIntervalMilliseconds))
            {
                var requestedPpm = AudioQueueRateControllerMath.CalculateAdjustmentPpm(
                    Volatile.Read(ref queueFrames), targetQueueFrames, periodFrames);
                if (Math.Abs(requestedPpm - appliedPpm) < MinimumChangePpm) continue;
                var sampleRate = nominalSampleRate * (1 + requestedPpm / 1_000_000d);
                activeAdjustment.SetSampleRate((float)sampleRate);
                appliedPpm = requestedPpm;
                Interlocked.Exchange(ref appliedPpmBits, BitConverter.DoubleToInt64Bits(appliedPpm));
            }
        }
        catch (COMException exception)
        {
            SafeWarning($"Windows audio queue-rate correction became unavailable (0x{exception.HResult:X8}).");
        }
        finally
        {
            initializedEvent.Set();
            Volatile.Write(ref available, 0);
            if (adjustment is not null && Marshal.IsComObject(adjustment)) Marshal.ReleaseComObject(adjustment);
            if (comInitialized) PInvoke.CoUninitialize();
        }
    }

    private void SafeWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }
}

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
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
    private const double MinimumChangePpm = 50;

    private readonly IAudioClient? renderClient;
    private readonly IApplicationLogger logger;
    private readonly double nominalSampleRate;
    private readonly int targetQueueFrames;
    private readonly int periodFrames;
    private readonly ManualResetEvent stopEvent = new(false);
    private readonly ManualResetEvent initializedEvent = new(false);
    private readonly Thread thread;
    private readonly Action<WaitHandle>? testWorker;
    private readonly TimeSpan stopTimeout;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int queueFrames;
    private long appliedPpmBits;
    private int available;
    private int stopRequested;
    private int handlesDisposed;

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
        stopTimeout = DefaultStopTimeout;
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Audio queue rate controller",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal AudioQueueRateController(
        Action<WaitHandle> testWorker,
        TimeSpan stopTimeout,
        IApplicationLogger? logger = null)
    {
        this.testWorker = testWorker ?? throw new ArgumentNullException(nameof(testWorker));
        ArgumentOutOfRangeException.ThrowIfLessThan(stopTimeout, TimeSpan.Zero);
        this.stopTimeout = stopTimeout;
        this.logger = logger ?? NullApplicationLogger.Instance;
        nominalSampleRate = 48_000;
        targetQueueFrames = 960;
        periodFrames = 480;
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Test audio queue rate controller",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    internal double? AppliedAdjustmentPpm => Volatile.Read(ref available) != 0
        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref appliedPpmBits))
        : null;

    internal bool IsAvailable => Volatile.Read(ref available) != 0;
    internal Task Completion => completion.Task;

    internal void UpdateQueueDepth(int value) => Volatile.Write(ref queueFrames, value);

    internal bool Start()
    {
        thread.Start();
        return initializedEvent.WaitOne(StartupTimeoutMilliseconds) && Volatile.Read(ref available) != 0;
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0) stopEvent.Set();
        if (!completion.Task.Wait(stopTimeout))
        {
            SafeWarning("The audio queue-rate controller did not stop within its two-second bound.");
            return false;
        }
        DisposeHandles();
        return true;
    }

    public void Dispose() => _ = Stop();

    private unsafe void ThreadMain()
    {
        IAudioClockAdjustment? adjustment = null;
        var comInitialized = false;
        try
        {
            if (testWorker is not null)
            {
                Volatile.Write(ref available, 1);
                initializedEvent.Set();
                testWorker(stopEvent);
                return;
            }

            var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartment.Failed) Marshal.ThrowExceptionForHR(apartment.Value);
            comInitialized = true;
            renderClient!.GetService(out adjustment);
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
        catch (Exception exception)
        {
            SafeWarning($"The audio queue-rate controller ended safely after an unexpected {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                initializedEvent.Set();
                Volatile.Write(ref available, 0);
                try
                {
                    if (adjustment is not null && Marshal.IsComObject(adjustment)) Marshal.ReleaseComObject(adjustment);
                }
                catch (Exception exception)
                {
                    SafeWarning($"Audio queue-rate adjustment cleanup failed safely ({exception.GetType().Name}).");
                }
                finally
                {
                    if (comInitialized) PInvoke.CoUninitialize();
                }
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }

    private void SafeWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }

    private void DisposeHandles()
    {
        if (Interlocked.Exchange(ref handlesDisposed, 1) != 0) return;
        initializedEvent.Dispose();
        stopEvent.Dispose();
    }
}

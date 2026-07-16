using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Infrastructure;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace HdmiCaptureCardMonitor.Capture.Audio;

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
    private readonly TaskCompletionSource workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int queueFrames;
    private long captureFrames;
    private long renderFrames;
    private long discontinuityCount;
    private long lateRenderWakeCount;
    private long requestedPpmBits;
    private long appliedPpmBits;
    private long estimatedDriftPpmBits;
    private long saturationTicks;
    private long activationTicks;
    private long directionChangeCount;
    private int available;
    private int adjustmentActive;
    private int workerStarted;
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
        _ = CompleteLifetimeAsync();
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
        _ = CompleteLifetimeAsync();
    }

    internal double? RequestedAdjustmentPpm => Volatile.Read(ref available) != 0
        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref requestedPpmBits))
        : null;
    internal double? AppliedAdjustmentPpm => Volatile.Read(ref available) != 0
        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref appliedPpmBits))
        : null;
    internal TimeSpan SaturationDuration => TimeSpan.FromTicks(Interlocked.Read(ref saturationTicks));
    internal TimeSpan ActivationDuration => TimeSpan.FromTicks(Interlocked.Read(ref activationTicks));
    internal long DirectionChangeCount => Interlocked.Read(ref directionChangeCount);
    internal double? EstimatedClockDriftPpm => Volatile.Read(ref available) != 0
        ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref estimatedDriftPpmBits))
        : null;
    internal bool IsAdjustmentActive => Volatile.Read(ref adjustmentActive) != 0;

    internal bool IsAvailable => Volatile.Read(ref available) != 0;
    internal Task Completion => completion.Task;
    internal bool HandlesReleased => Volatile.Read(ref handlesDisposed) != 0;

    internal void UpdateObservation(
        int queuedFrames,
        long capturedFrames,
        long renderedFrames,
        long discontinuities,
        long lateRenderWakes)
    {
        Volatile.Write(ref queueFrames, queuedFrames);
        Interlocked.Exchange(ref captureFrames, capturedFrames);
        Interlocked.Exchange(ref renderFrames, renderedFrames);
        Interlocked.Exchange(ref discontinuityCount, discontinuities);
        Interlocked.Exchange(ref lateRenderWakeCount, lateRenderWakes);
    }

    internal bool Start()
    {
        if (Interlocked.Exchange(ref workerStarted, 1) != 0)
            throw new InvalidOperationException("The audio queue-rate controller can start only once.");
        thread.Start();
        try
        {
            return initializedEvent.WaitOne(StartupTimeoutMilliseconds) && Volatile.Read(ref available) != 0;
        }
        finally
        {
            startFinished.TrySetResult();
        }
    }

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0)
        {
            try { stopEvent.Set(); }
            catch (ObjectDisposedException) when (completion.Task.IsCompleted) { }
        }
        if (Volatile.Read(ref workerStarted) == 0)
        {
            startFinished.TrySetResult();
            workerExited.TrySetResult();
        }
        if (!completion.Task.Wait(stopTimeout))
        {
            SafeWarning("The audio queue-rate controller did not stop within its two-second bound.");
            return false;
        }
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

            var policy = new AudioQueueRateControllerPolicy(targetQueueFrames, periodFrames);
            var appliedPpm = 0d;
            var interval = TimeSpan.FromMilliseconds(ControlIntervalMilliseconds);
            while (!stopEvent.WaitOne(ControlIntervalMilliseconds))
            {
                var decision = policy.Step(new AudioQueueRateControllerSample(
                    Volatile.Read(ref queueFrames),
                    Interlocked.Read(ref captureFrames),
                    Interlocked.Read(ref renderFrames),
                    Interlocked.Read(ref discontinuityCount),
                    Interlocked.Read(ref lateRenderWakeCount)), interval);
                Interlocked.Exchange(ref requestedPpmBits, BitConverter.DoubleToInt64Bits(decision.RequestedAdjustmentPpm));
                Interlocked.Exchange(ref estimatedDriftPpmBits, BitConverter.DoubleToInt64Bits(decision.EstimatedClockDriftPpm ?? 0));
                Interlocked.Exchange(ref saturationTicks, decision.SaturationDuration.Ticks);
                Interlocked.Exchange(ref activationTicks, decision.ActivationDuration.Ticks);
                Interlocked.Exchange(ref directionChangeCount, decision.DirectionChangeCount);
                Volatile.Write(ref adjustmentActive, decision.IsAdjustmentActive ? 1 : 0);
                if (Math.Abs(decision.AppliedAdjustmentPpm - appliedPpm) < double.Epsilon) continue;
                var sampleRate = nominalSampleRate * (1 + decision.AppliedAdjustmentPpm / 1_000_000d);
                activeAdjustment.SetSampleRate((float)sampleRate);
                appliedPpm = decision.AppliedAdjustmentPpm;
                Interlocked.Exchange(ref appliedPpmBits, BitConverter.DoubleToInt64Bits(appliedPpm));
            }

            if (Math.Abs(appliedPpm) >= double.Epsilon)
            {
                activeAdjustment.SetSampleRate((float)nominalSampleRate);
                Interlocked.Exchange(ref requestedPpmBits, BitConverter.DoubleToInt64Bits(0));
                Interlocked.Exchange(ref appliedPpmBits, BitConverter.DoubleToInt64Bits(0));
                Volatile.Write(ref adjustmentActive, 0);
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
                workerExited.TrySetResult();
            }
        }
    }

    private async Task CompleteLifetimeAsync()
    {
        await Task.WhenAll(workerExited.Task, startFinished.Task).ConfigureAwait(false);
        DisposeHandles();
        completion.TrySetResult();
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

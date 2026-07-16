using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HdmiCaptureCardMonitor.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal sealed class WasapiAudioMonitorSession : IAudioMonitorSession
{
    private const int StartupTimeoutSeconds = 5;
    private const int StopTimeoutSeconds = 3;
    private const int BufferPeriodCapacity = 6;
    private const int HighWatermarkPeriods = 6;
    private const int DefaultTargetQueuePeriods = 2;
    private const uint WaitMilliseconds = 1000;

    private readonly AudioMonitorStartRequest request;
    private readonly IApplicationLogger logger;
    private readonly AudioGainController gain;
    private readonly bool preferAudioClient3;
    private readonly int targetQueuePeriods;
    private readonly Thread worker;
    private readonly AudioControlEventPublisher controlEventPublisher;
    private readonly LatestAudioDiagnosticsPublisher diagnosticsPublisher;
    private readonly TaskCompletionSource<AudioMonitorStartResult> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource packetWorkerSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource auxiliaryWorkersSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SafeFileHandle? stopEvent;
    private int state = (int)AudioMonitorState.Off;
    private int started;
    private int stopRequested;
    private int disposed;

    internal WasapiAudioMonitorSession(
        AudioMonitorStartRequest request,
        IApplicationLogger logger,
        bool preferAudioClient3 = true,
        int targetQueuePeriods = DefaultTargetQueuePeriods)
    {
        this.request = request;
        this.logger = logger;
        this.preferAudioClient3 = preferAudioClient3;
        if (targetQueuePeriods is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(targetQueuePeriods), "The queue target must be two or three periods.");
        this.targetQueuePeriods = targetQueuePeriods;
        gain = new AudioGainController();
        gain.SetVolume(request.InitialVolumePercent);
        gain.SetMuted(request.InitiallyMuted);
        SessionId = Guid.NewGuid();
        worker = new Thread(WorkerEntry)
        {
            IsBackground = true,
            Name = $"Audio monitor {SessionId:N}",
            Priority = ThreadPriority.AboveNormal
        };
        worker.SetApartmentState(ApartmentState.MTA);
        controlEventPublisher = new AudioControlEventPublisher(
            args => SafeAudioEventDispatch.Publish(
                StateChanged, this, args, this.logger, "state"),
            args => SafeAudioEventDispatch.Publish(
                MonitoringFailed, this, args, this.logger, "failure"));
        diagnosticsPublisher = new LatestAudioDiagnosticsPublisher(args =>
            SafeAudioEventDispatch.Publish(
                DiagnosticsUpdated, this, args, this.logger, "diagnostics"));
    }

    public Guid SessionId { get; }
    public AudioMonitorState State => (AudioMonitorState)Volatile.Read(ref state);
    public Task Completion => completion.Task;

    public event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged;
    public event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated;
    public event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed;

    public async Task<AudioMonitorStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return await startup.Task.ConfigureAwait(false);
        controlEventPublisher.Start();
        diagnosticsPublisher.Start();
        ChangeState(AudioMonitorState.Starting);
        worker.Start();
        _ = CompleteWhenAllWorkersSettleAsync();
        try
        {
            return await startup.Task.WaitAsync(TimeSpan.FromSeconds(StartupTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RequestStop();
            return AudioMonitorStartResult.Cancelled(SessionId);
        }
        catch (TimeoutException exception)
        {
            RequestStop();
            return AudioMonitorStartResult.Failed(SessionId, new AudioMonitorFailure(
                AudioMonitorFailureCategory.StartupTimeout,
                "Audio monitoring took too long to start.",
                null,
                exception));
        }
    }

    public async Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref started) == 0) return AudioMonitorStopResult.Stopped;
        RequestStop();
        if (State is AudioMonitorState.Starting or AudioMonitorState.Monitoring or AudioMonitorState.Muted)
            ChangeState(AudioMonitorState.Stopping);
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(StopTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return AudioMonitorStopResult.Stopped;
        }
        catch (OperationCanceledException)
        {
            return AudioMonitorStopResult.Failed(new AudioMonitorFailure(
                AudioMonitorFailureCategory.StopTimeout,
                "Audio monitoring stop was cancelled."));
        }
        catch (TimeoutException exception)
        {
            return AudioMonitorStopResult.Timeout(new AudioMonitorFailure(
                AudioMonitorFailureCategory.StopTimeout,
                "Audio monitoring did not stop within three seconds.",
                null,
                exception));
        }
    }

    public void SetVolume(double volumePercent) => gain.SetVolume(volumePercent);

    public void SetMuted(bool muted)
    {
        gain.SetMuted(muted);
        if (State is AudioMonitorState.Monitoring or AudioMonitorState.Muted)
            ChangeState(muted ? AudioMonitorState.Muted : AudioMonitorState.Monitoring);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        _ = await StopAsync().ConfigureAwait(false);
    }

    private unsafe void WorkerMain()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? captureDevice = null;
        IMMDevice? renderDevice = null;
        IAudioClient? captureClient = null;
        IAudioClient? renderClient = null;
        IAudioCaptureClient? captureService = null;
        IAudioRenderClient? renderService = null;
        AudioQueueRateController? rateController = null;
        WAVEFORMATEX* renderMix = null;
        WAVEFORMATEX* captureMix = null;
        SafeFileHandle? captureEvent = null;
        SafeFileHandle? renderEvent = null;
        SafeFileHandle? localStopEvent = null;
        AvRevertMmThreadCharacteristicsSafeHandle? mmcss = null;
        var comInitialized = false;
        var captureStarted = false;
        var renderStarted = false;
        var rateControllerStopped = true;
        try
        {
            var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartment.Failed) Marshal.ThrowExceptionForHR(apartment.Value);
            comInitialized = true;

            localStopEvent = PInvoke.CreateEvent(null, true, Volatile.Read(ref stopRequested) != 0, null);
            captureEvent = PInvoke.CreateEvent(null, false, false, null);
            renderEvent = PInvoke.CreateEvent(null, false, false, null);
            if (localStopEvent.IsInvalid || captureEvent.IsInvalid || renderEvent.IsInvalid)
                throw new AudioSessionException(AudioMonitorFailureCategory.EndpointCreateFailed, "Windows could not create audio synchronization events.");
            Volatile.Write(ref stopEvent, localStopEvent);

            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDevice(request.CaptureEndpoint.Id, out captureDevice);
            if (request.RenderEndpoint is null)
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out renderDevice);
            else
                enumerator.GetDevice(request.RenderEndpoint.Id, out renderDevice);

            IAudioClient? metadataCaptureClient = null;
            IAudioClient? metadataRenderClient = null;
            try
            {
                metadataCaptureClient = ActivateAudioClient(captureDevice);
                metadataRenderClient = ActivateAudioClient(renderDevice);
                metadataRenderClient.GetMixFormat(out renderMix);
                metadataCaptureClient.GetMixFormat(out captureMix);
            }
            finally
            {
                TryCleanup(() => ReleaseComObject(metadataCaptureClient), "metadata capture-client release");
                TryCleanup(() => ReleaseComObject(metadataRenderClient), "metadata render-client release");
            }
            var common = CreateCommonFormat(renderMix);
            var managedFormat = AudioStreamFormat.CreateIeeeFloat(
                checked((int)common.Format.nSamplesPerSec), common.Format.nChannels, common.dwChannelMask);

            var pairFactory = new AudioClientPairAttemptFactory(captureDevice, renderDevice, common);
            var initializedPair = AudioInitializationCoordinator.Initialize(
                preferAudioClient3 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240),
                pairFactory.Create,
                IsHybridFallbackEligible,
                exception => SafeWarning($"Hybrid audio initialization used a fresh classic pair after {DescribeTechnicalFailure(exception)}."));
            captureClient = initializedPair.CaptureClient;
            renderClient = initializedPair.RenderClient;
            var path = initializedPair.Path;
            var capturePeriod = initializedPair.CapturePeriod;
            var renderPeriod = initializedPair.RenderPeriod;

            captureClient.GetBufferSize(out var captureBufferFramesNative);
            renderClient.GetBufferSize(out var renderBufferFramesNative);
            var captureBufferFrames = checked((int)captureBufferFramesNative);
            var renderBufferFrames = checked((int)renderBufferFramesNative);
            captureClient.SetEventHandle(captureEvent);
            renderClient.SetEventHandle(renderEvent);
            captureClient.GetService(out captureService);
            renderClient.GetService(out renderService);
            var capturePacketService = captureService ?? throw new AudioSessionException(
                AudioMonitorFailureCategory.EndpointCreateFailed, "Windows did not provide an audio capture service.");
            var renderPacketService = renderService ?? throw new AudioSessionException(
                AudioMonitorFailureCategory.EndpointCreateFailed, "Windows did not provide an audio render service.");
            var captureBufferAccess = new WasapiCaptureBufferAccess(capturePacketService);
            var renderBufferAccess = new WasapiRenderBufferAccess(renderClient, renderPacketService);
            mmcss = TryRegisterMmcss();

            var stablePeriod = Math.Max(Math.Max(capturePeriod, renderPeriod), 1);
            var capacityFrames = checked(stablePeriod * BufferPeriodCapacity);
            var highWatermark = checked(stablePeriod * HighWatermarkPeriods);
            var startupTarget = Math.Min(checked(stablePeriod * targetQueuePeriods), highWatermark);
            var ring = new AudioFrameRingBuffer(capacityFrames, managedFormat.ChannelCount, highWatermark);
            rateController = new AudioQueueRateController(
                renderClient, logger, managedFormat.SampleRate, startupTarget, stablePeriod);
            if (!rateController.Start())
                throw new AudioSessionException(AudioMonitorFailureCategory.UnsupportedAudioFormat,
                    "The selected output does not support stable shared-mode audio monitoring.");

            var prefillFrames = checked((uint)Math.Min(renderPeriod, renderBufferFrames));
            AudioNativeBufferProcessor.PrefillRenderWithSilence(
                renderBufferAccess, managedFormat.ChannelCount, prefillFrames);
            captureClient.Start();
            captureStarted = true;

            long framesCaptured = 0;
            long framesRendered = 0;
            long silentFrames = prefillFrames;
            long discontinuities = 0;
            long timestampErrors = 0;
            ulong lastDevicePosition = 0;
            ulong lastQpcPosition = 0;
            DateTimeOffset? lastPacket = null;
            DateTimeOffset? lastRender = null;
            long queueSampleTotal = 0;
            long queueSampleCount = 0;
            var discontinuityTracker = new AudioDiscontinuityTracker();
            var sessionClock = Stopwatch.StartNew();
            var transitionClock = new Stopwatch();
            var discontinuityPhase = AudioDiscontinuityPhase.Startup;
            void RecordDiscontinuity(AudioCapturePacket packet, int queueBefore, int queueAfter)
            {
                discontinuityTracker.Observe(
                    sessionClock.Elapsed, packet, queueBefore, queueAfter,
                    rateController.RequestedAdjustmentPpm, rateController.AppliedAdjustmentPpm,
                    ring.UnderrunCount, ring.OverrunCount, discontinuityPhase);
            }
            var diagnosticsClock = Stopwatch.StartNew();
            var startupHandles = new[] { ToHandle(localStopEvent), ToHandle(captureEvent) };

            while (ring.QueuedFrames < startupTarget && Volatile.Read(ref stopRequested) == 0)
            {
                var wait = PInvoke.WaitForMultipleObjects(startupHandles, false, WaitMilliseconds);
                if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
                if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1))
                    ProcessCapture(captureBufferAccess, ring, managedFormat, ref framesCaptured, ref silentFrames,
                        ref discontinuities, ref timestampErrors, ref lastDevicePosition, ref lastQpcPosition, ref lastPacket,
                        RecordDiscontinuity);
                else if (wait == WAIT_EVENT.WAIT_FAILED)
                    throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio event waiting failed.");
                rateController.UpdateQueueDepth(ring.QueuedFrames);
                queueSampleTotal += ring.QueuedFrames;
                queueSampleCount++;
            }

            if (Volatile.Read(ref stopRequested) != 0)
            {
                startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
                return;
            }

            renderClient.Start();
            renderStarted = true;
            discontinuityPhase = AudioDiscontinuityPhase.Transition;
            transitionClock.Start();
            ChangeState(gain.IsMuted ? AudioMonitorState.Muted : AudioMonitorState.Monitoring);
            startup.TrySetResult(AudioMonitorStartResult.Started(SessionId));
            SafeInformation($"Audio monitoring started for {request.CaptureEndpoint.DisplayName} to " +
                $"{request.RenderEndpoint?.DisplayName ?? "System default output"}; {managedFormat}; {path}; " +
                $"periods {capturePeriod}/{renderPeriod} frames; queue capacity {capacityFrames} frames.");

            // A render wake drains every capture packet already available before
            // reserving one output period. A capture-only wake does not render early;
            // this prevents both fixed-priority starvation and premature silence.
            var monitoringHandles = new[] { ToHandle(localStopEvent), ToHandle(renderEvent), ToHandle(captureEvent) };

            while (Volatile.Read(ref stopRequested) == 0)
            {
                if (discontinuityPhase == AudioDiscontinuityPhase.Transition &&
                    transitionClock.Elapsed >= TimeSpan.FromSeconds(2))
                    discontinuityPhase = AudioDiscontinuityPhase.SteadyState;
                var wait = PInvoke.WaitForMultipleObjects(monitoringHandles, false, WaitMilliseconds);
                if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
                if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1))
                {
                    // A capture event may already be signalled when the render period wakes us.
                    // Drain it first so a complete packet that is ready now cannot be mistaken for
                    // an underrun merely because the render handle has the lower wait index.
                    ProcessCapture(captureBufferAccess, ring, managedFormat, ref framesCaptured, ref silentFrames,
                        ref discontinuities, ref timestampErrors, ref lastDevicePosition, ref lastQpcPosition, ref lastPacket,
                        RecordDiscontinuity);
                    ProcessRender(renderBufferAccess, ring, managedFormat, gain, renderBufferFramesNative, renderPeriod,
                        ref framesRendered, ref silentFrames, ref lastRender);
                }
                else if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 2))
                    ProcessCapture(captureBufferAccess, ring, managedFormat, ref framesCaptured, ref silentFrames,
                        ref discontinuities, ref timestampErrors, ref lastDevicePosition, ref lastQpcPosition, ref lastPacket,
                        RecordDiscontinuity);
                else if (wait == WAIT_EVENT.WAIT_FAILED)
                    throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio event waiting failed.");

                rateController.UpdateQueueDepth(ring.QueuedFrames);
                queueSampleTotal += ring.QueuedFrames;
                queueSampleCount++;
                discontinuityTracker.UpdateOutcomes(
                    sessionClock.Elapsed, ring.UnderrunCount, ring.OverrunCount);
                if (!rateController.IsAvailable)
                    throw new AudioSessionException(AudioMonitorFailureCategory.AudioProcessingFailure,
                        "Windows audio rate adjustment became unavailable.");

                if (diagnosticsClock.ElapsedMilliseconds >= 500)
                {
                    diagnosticsClock.Restart();
                    var discontinuitySnapshot = discontinuityTracker.Snapshot();
                    diagnosticsPublisher.PublishLatest(new AudioMonitorDiagnosticsEventArgs(SessionId, new AudioMonitorDiagnostics(
                        SessionId,
                        request.CaptureEndpoint.DisplayName,
                        request.RenderEndpoint?.DisplayName ?? "System default output",
                        request.RenderEndpoint is null,
                        DescribeFormat(captureMix),
                        DescribeFormat(renderMix),
                        managedFormat,
                        path,
                        capturePeriod,
                        renderPeriod,
                        captureBufferFrames,
                        renderBufferFrames,
                        ring.QueuedFrames,
                        ring.MaximumQueuedFrames,
                        framesCaptured,
                        framesRendered,
                        silentFrames,
                        discontinuities,
                        timestampErrors,
                        ring.UnderrunCount,
                        ring.OverrunCount,
                        ring.DroppedFrames,
                        gain.VolumePercent,
                        gain.IsMuted,
                        lastPacket,
                        lastRender,
                        null,
                        rateController.AppliedAdjustmentPpm,
                        LastCaptureDevicePosition: lastDevicePosition,
                        LastCaptureQpcPosition: lastQpcPosition,
                        MmcssRegistered: mmcss is not null,
                        RingBufferCapacityFrames: capacityFrames,
                        TargetQueueFrames: startupTarget,
                        AverageQueueFrames: queueSampleCount == 0 ? 0 : queueSampleTotal / (double)queueSampleCount,
                        RequestedRateAdjustment: rateController.RequestedAdjustmentPpm,
                        RateAdjustmentSaturationMilliseconds: checked((long)rateController.SaturationDuration.TotalMilliseconds),
                        RateAdjustmentDirectionChangeCount: rateController.DirectionChangeCount,
                        DiscontinuityTimeline: discontinuitySnapshot)));
                }
            }
        }
        catch (Exception exception) when (exception is COMException or AudioSessionException or OverflowException)
        {
            var failure = CreateFailure(exception);
            ChangeState(AudioMonitorState.Faulted);
            startup.TrySetResult(AudioMonitorStartResult.Failed(SessionId, failure));
            _ = controlEventPublisher.PublishFailure(
                new AudioMonitorFailureEventArgs(SessionId, failure));
            SafeWarning($"Audio monitoring failed safely with {failure.Category}" +
                (failure.HResult is int hresult ? $" (0x{hresult:X8})." : "."));
        }
        finally
        {
            startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
            var diagnosticsPublisherStopped = false;
            try
            {
                rateControllerStopped = rateController is null || TryStopRateController(rateController);
                diagnosticsPublisherStopped = TryStopDiagnosticsPublisher();
                TryCleanup(() => { if (captureStarted) captureClient?.Stop(); }, "capture stop");
                TryCleanup(() => { if (renderStarted) renderClient?.Stop(); }, "render stop");
                TryCleanup(() => ReleaseComObject(captureService), "capture-service release");
                TryCleanup(() => ReleaseComObject(renderService), "render-service release");
                TryCleanup(() => ReleaseComObject(captureClient), "capture-client release");
                if (rateControllerStopped)
                    TryCleanup(() => ReleaseComObject(renderClient), "render-client release");
                else
                    SafeWarning("The render client remains owned until its queue-rate controller settles.");
                TryCleanup(() => ReleaseComObject(captureDevice), "capture-endpoint release");
                TryCleanup(() => ReleaseComObject(renderDevice), "render-endpoint release");
                TryCleanup(() => ReleaseComObject(enumerator), "endpoint-enumerator release");
                Volatile.Write(ref stopEvent, null);
                TryCleanup(() => captureEvent?.Dispose(), "capture-event disposal");
                TryCleanup(() => renderEvent?.Dispose(), "render-event disposal");
                TryCleanup(() => localStopEvent?.Dispose(), "stop-event disposal");
                TryCleanup(() => { if (captureMix is not null) PInvoke.CoTaskMemFree(captureMix); }, "capture-format release");
                TryCleanup(() => { if (renderMix is not null) PInvoke.CoTaskMemFree(renderMix); }, "render-format release");
                TryCleanup(() => mmcss?.Dispose(), "MMCSS revert");
                TryCleanup(() => { if (comInitialized) PInvoke.CoUninitialize(); }, "COM uninitialization");
            }
            finally
            {
                if (rateControllerStopped && diagnosticsPublisherStopped)
                {
                    CompleteAuxiliaryWorkers();
                }
                else
                {
                    var controllerCompletion = rateController?.Completion ?? Task.CompletedTask;
                    var retainedRenderClient = rateControllerStopped ? null : renderClient;
                    _ = CompleteAfterWorkersSettleAsync(
                        controllerCompletion,
                        diagnosticsPublisher.Completion,
                        retainedRenderClient);
                }
            }
        }
    }

    private static IAudioClient ActivateAudioClient(IMMDevice device)
    {
        device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out var value);
        return (IAudioClient)value;
    }

    private static unsafe InitializedAudioClientPair CreateAndInitializeClientPair(
        IMMDevice captureDevice,
        IMMDevice renderDevice,
        WAVEFORMATEXTENSIBLE* format,
        AudioInitializationMode mode)
    {
        IAudioClient? capture = null;
        IAudioClient? render = null;
        try
        {
            capture = ActivateAudioClient(captureDevice);
            render = ActivateAudioClient(renderDevice);
            AudioMonitorInitializationPath path;
            int capturePeriod;
            int renderPeriod;
            if (mode == AudioInitializationMode.Hybrid)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                    throw new AudioInitializationFallbackException("AudioClient3 requires Windows 10.");
                if (!TryInitializeHybridPair(capture, render, format, out capturePeriod, out renderPeriod))
                    throw new AudioInitializationFallbackException("AudioClient3 shared-period initialization is unavailable.");
                path = AudioMonitorInitializationPath.AudioClient3CaptureClassicRender;
            }
            else
            {
                path = InitializeClassicPair(capture, render, format, out capturePeriod, out renderPeriod);
            }

            return new InitializedAudioClientPair(capture, render, path, capturePeriod, renderPeriod);
        }
        catch
        {
            ReleaseComObject(capture);
            ReleaseComObject(render);
            throw;
        }
    }

    internal static bool IsHybridFallbackEligible(Exception exception)
    {
        if (exception is AudioInitializationFallbackException) return true;
        if (exception is not COMException comException) return false;
        return IsHybridFallbackEligibleHResult(comException.HResult);
    }

    internal static bool IsHybridFallbackEligibleHResult(int hresult) =>
        MapFailureCategory(hresult) is not (
            AudioMonitorFailureCategory.AccessDenied or
            AudioMonitorFailureCategory.DeviceInvalidated or
            AudioMonitorFailureCategory.ResourcesInvalidated or
            AudioMonitorFailureCategory.AudioServiceNotRunning or
            AudioMonitorFailureCategory.DeviceInUse);

    private static string DescribeTechnicalFailure(Exception exception) => exception is COMException comException
        ? $"HRESULT 0x{comException.HResult:X8}"
        : exception.GetType().Name;

    private static unsafe WAVEFORMATEXTENSIBLE CreateCommonFormat(WAVEFORMATEX* outputMix)
    {
        if (outputMix is null || outputMix->nSamplesPerSec == 0 || outputMix->nChannels == 0 ||
            outputMix->nBlockAlign == 0 || outputMix->nAvgBytesPerSec == 0)
            throw new AudioSessionException(AudioMonitorFailureCategory.UnsupportedAudioFormat, "The selected output exposes an invalid audio mix format.");
        var blockAlign = checked((ushort)(outputMix->nChannels * sizeof(float)));
        var channelMask = outputMix->nChannels switch { 1 => 0x4u, 2 => 0x3u, _ => 0u };
        if (outputMix->wFormatTag == PInvoke.WAVE_FORMAT_EXTENSIBLE && outputMix->cbSize >= 22)
        {
            var extensible = (WAVEFORMATEXTENSIBLE*)outputMix;
            if (extensible->dwChannelMask != 0) channelMask = extensible->dwChannelMask;
        }
        return new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = checked((ushort)PInvoke.WAVE_FORMAT_EXTENSIBLE),
                nChannels = outputMix->nChannels,
                nSamplesPerSec = outputMix->nSamplesPerSec,
                nAvgBytesPerSec = checked(outputMix->nSamplesPerSec * blockAlign),
                nBlockAlign = blockAlign,
                wBitsPerSample = 32,
                cbSize = 22
            },
            Samples = new WAVEFORMATEXTENSIBLE._Samples_e__Union { wValidBitsPerSample = 32 },
            dwChannelMask = channelMask,
            SubFormat = new Guid("00000003-0000-0010-8000-00AA00389B71")
        };
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static unsafe bool TryInitializeHybridPair(
        IAudioClient capture,
        IAudioClient render,
        WAVEFORMATEXTENSIBLE* format,
        out int capturePeriod,
        out int renderPeriod)
    {
        capturePeriod = 0;
        renderPeriod = 0;
        if (capture is not IAudioClient3 capture3) return false;
        if (!IsExactlySupported(capture, &format->Format) || !IsExactlySupported(render, &format->Format)) return false;
        capture3.GetSharedModeEnginePeriod(&format->Format, out var captureDefault, out var captureFundamental, out var captureMinimum, out _);
        var period = RoundDown(captureDefault, captureFundamental);
        if (period < captureMinimum || period > captureDefault) return false;
        capture3.InitializeSharedAudioStream(PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, &format->Format, null);

        render.GetDevicePeriod(out var renderDefaultDuration, out _);
        var capturePeriodDuration = checked((long)Math.Ceiling(period * 10_000_000d / format->Format.nSamplesPerSec));
        var renderDuration = Math.Max(renderDefaultDuration, capturePeriodDuration);
        var renderFlags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
            PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
            PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY |
            PInvoke.AUDCLNT_STREAMFLAGS_RATEADJUST;
        render.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, renderFlags, renderDuration, 0, &format->Format, null);
        capturePeriod = checked((int)period);
        renderPeriod = ReadCurrentRenderPeriod(render, format->Format.nSamplesPerSec, renderDefaultDuration);
        return true;
    }

    private static unsafe AudioMonitorInitializationPath InitializeClassicPair(
        IAudioClient capture,
        IAudioClient render,
        WAVEFORMATEXTENSIBLE* format,
        out int capturePeriod,
        out int renderPeriod)
    {
        capture.GetDevicePeriod(out var captureDefault, out _);
        render.GetDevicePeriod(out var renderDefault, out _);
        var duration = Math.Max(captureDefault, renderDefault) * 2;
        var captureFlags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
            PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
            PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        var renderFlags = captureFlags | PInvoke.AUDCLNT_STREAMFLAGS_RATEADJUST;
        capture.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, captureFlags, duration, 0, &format->Format, null);
        render.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, renderFlags, duration, 0, &format->Format, null);
        capture.GetBufferSize(out var captureFrames);
        render.GetBufferSize(out _);
        capturePeriod = checked((int)Math.Max(1, captureFrames / 2));
        renderPeriod = ReadCurrentRenderPeriod(render, format->Format.nSamplesPerSec, renderDefault);
        return AudioMonitorInitializationPath.ClassicSharedFallback;
    }

    private static unsafe int ReadCurrentRenderPeriod(IAudioClient render, uint sampleRate, long defaultDuration)
    {
        if (render is IAudioClient3 render3 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            WAVEFORMATEX* currentFormat = null;
            try
            {
                render3.GetCurrentSharedModeEnginePeriod(out currentFormat, out var currentPeriod);
                if (currentPeriod > 0) return checked((int)currentPeriod);
            }
            catch (COMException) { }
            finally { if (currentFormat is not null) PInvoke.CoTaskMemFree(currentFormat); }
        }

        var frames = checked((long)Math.Round(defaultDuration * sampleRate / 10_000_000d, MidpointRounding.AwayFromZero));
        return checked((int)Math.Max(1, frames));
    }

    private static unsafe bool IsExactlySupported(IAudioClient client, WAVEFORMATEX* format)
    {
        WAVEFORMATEX* closest = null;
        try { return client.IsFormatSupported(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, format, &closest).Value == 0; }
        finally { if (closest is not null) PInvoke.CoTaskMemFree(closest); }
    }

    private static void ProcessCapture(
        IAudioCaptureBufferAccess access,
        AudioFrameRingBuffer ring,
        AudioStreamFormat format,
        ref long framesCaptured,
        ref long silentFrames,
        ref long discontinuities,
        ref long timestampErrors,
        ref ulong lastDevicePosition,
        ref ulong lastQpcPosition,
        ref DateTimeOffset? lastPacket,
        Action<AudioCapturePacket, int, int>? discontinuityObserved = null)
    {
        var result = AudioNativeBufferProcessor.ProcessCapture(
            access, ring, format, discontinuityObserved);
        framesCaptured += result.FramesCaptured;
        silentFrames += result.SilentFrames;
        discontinuities += result.Discontinuities;
        timestampErrors += result.TimestampErrors;
        if (result.HasPacket)
        {
            lastDevicePosition = result.LastDevicePosition;
            lastQpcPosition = result.LastQpcPosition;
            lastPacket = DateTimeOffset.UtcNow;
        }
    }

    private static void ProcessRender(
        IAudioRenderBufferAccess access,
        AudioFrameRingBuffer ring,
        AudioStreamFormat format,
        AudioGainController gain,
        uint bufferFrames,
        int renderPeriodFrames,
        ref long framesRendered,
        ref long silentFrames,
        ref DateTimeOffset? lastRender)
    {
        var result = AudioNativeBufferProcessor.ProcessRender(
            access, ring, format, gain, bufferFrames, renderPeriodFrames);
        if (!result.Rendered) return;
        framesRendered += result.Frames;
        silentFrames += result.SilentFrames;
        lastRender = DateTimeOffset.UtcNow;
    }

    private static unsafe string DescribeFormat(WAVEFORMATEX* format) => format is null
        ? "Unavailable"
        : $"{format->nSamplesPerSec} Hz, {format->nChannels} channels, {format->wBitsPerSample}-bit, tag 0x{format->wFormatTag:X4}";

    private static AvRevertMmThreadCharacteristicsSafeHandle? TryRegisterMmcss()
    {
        try
        {
            uint taskIndex = 0;
            var handle = PInvoke.AvSetMmThreadCharacteristics("Audio", ref taskIndex);
            if (!handle.IsInvalid) _ = PInvoke.AvSetMmThreadPriority(handle, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);
            return handle.IsInvalid ? null : handle;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static uint RoundDown(uint value, uint multiple) => multiple == 0 ? 0 : value / multiple * multiple;

    private static unsafe HANDLE ToHandle(SafeHandle handle) => new((void*)handle.DangerousGetHandle());

    private void RequestStop()
    {
        Interlocked.Exchange(ref stopRequested, 1);
        var handle = Volatile.Read(ref stopEvent);
        if (handle is not null && !handle.IsInvalid && !handle.IsClosed) _ = PInvoke.SetEvent(handle);
    }

    private void ChangeState(AudioMonitorState next)
    {
        var previous = (AudioMonitorState)Interlocked.Exchange(ref state, (int)next);
        if (previous != next)
            _ = controlEventPublisher.PublishState(
                new AudioMonitorStateChangedEventArgs(SessionId, previous, next));
    }

    private static AudioMonitorFailure CreateFailure(Exception exception)
    {
        if (exception is AudioSessionException audio)
            return new AudioMonitorFailure(audio.Category, audio.Message, null, audio);
        var hresult = exception.HResult;
        var category = MapFailureCategory(hresult);
        var message = category switch
        {
            AudioMonitorFailureCategory.AccessDenied => "Microphone access is blocked. Allow desktop apps to use the microphone in Windows Privacy & security.",
            AudioMonitorFailureCategory.AudioServiceNotRunning => "Windows Audio is not running.",
            AudioMonitorFailureCategory.DeviceInUse => "The selected audio device is busy. Close other apps using it and try again.",
            AudioMonitorFailureCategory.DeviceInvalidated or AudioMonitorFailureCategory.ResourcesInvalidated => "The selected audio device disconnected or changed.",
            _ => "Audio monitoring stopped because Windows reported an audio processing failure."
        };
        return new AudioMonitorFailure(category, message, hresult, exception);
    }

    private static AudioMonitorFailureCategory MapFailureCategory(int hresult) => hresult switch
    {
        unchecked((int)0x80070005) => AudioMonitorFailureCategory.AccessDenied,
        unchecked((int)0x88890004) => AudioMonitorFailureCategory.DeviceInvalidated,
        unchecked((int)0x88890008) => AudioMonitorFailureCategory.UnsupportedAudioFormat,
        unchecked((int)0x8889000A) => AudioMonitorFailureCategory.DeviceInUse,
        unchecked((int)0x88890010) => AudioMonitorFailureCategory.AudioServiceNotRunning,
        unchecked((int)0x88890026) => AudioMonitorFailureCategory.ResourcesInvalidated,
        _ => AudioMonitorFailureCategory.AudioProcessingFailure
    };

    private sealed record InitializedAudioClientPair(
        IAudioClient CaptureClient,
        IAudioClient RenderClient,
        AudioMonitorInitializationPath Path,
        int CapturePeriod,
        int RenderPeriod);

    private sealed class AudioClientPairAttemptFactory(
        IMMDevice captureDevice,
        IMMDevice renderDevice,
        WAVEFORMATEXTENSIBLE format)
    {
        private readonly IMMDevice captureDevice = captureDevice;
        private readonly IMMDevice renderDevice = renderDevice;
        private readonly WAVEFORMATEXTENSIBLE format = format;

        internal unsafe InitializedAudioClientPair Create(AudioInitializationMode mode)
        {
            var localFormat = format;
            return CreateAndInitializeClientPair(captureDevice, renderDevice, &localFormat, mode);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private void TryCleanup(Action action, string operation)
    {
        try { action(); }
        catch (Exception exception)
        {
            SafeWarning($"Audio {operation} cleanup failed safely ({exception.GetType().Name}); later cleanup continued.");
        }
    }

    private bool TryStopRateController(AudioQueueRateController controller)
    {
        try { return controller.Stop(); }
        catch (Exception exception)
        {
            SafeWarning($"Audio queue-rate-controller stop failed safely ({exception.GetType().Name}).");
            return controller.Completion.IsCompleted;
        }
    }

    private bool TryStopDiagnosticsPublisher()
    {
        try { return diagnosticsPublisher.Stop(); }
        catch (Exception exception)
        {
            SafeWarning($"Audio diagnostics-publisher stop failed safely ({exception.GetType().Name}).");
            return diagnosticsPublisher.Completion.IsCompleted;
        }
    }

    private bool TryStopControlEventPublisher()
    {
        try { return controlEventPublisher.Stop(); }
        catch (Exception exception)
        {
            SafeWarning($"Audio control-event-publisher stop failed safely ({exception.GetType().Name}).");
            return controlEventPublisher.Completion.IsCompleted;
        }
    }

    private async Task CompleteAfterWorkersSettleAsync(
        Task controllerCompletion,
        Task publisherCompletion,
        object? retainedRenderClient)
    {
        try { await Task.WhenAll(controllerCompletion, publisherCompletion).ConfigureAwait(false); }
        catch (Exception exception)
        {
            SafeWarning($"Audio auxiliary-worker completion failed safely ({exception.GetType().Name}).");
        }
        finally
        {
            var comInitialized = false;
            try
            {
                if (retainedRenderClient is not null)
                {
                    comInitialized = TryInitializeMta();
                    TryCleanup(() => ReleaseComObject(retainedRenderClient), "retained render-client release");
                }
            }
            finally
            {
                if (comInitialized) PInvoke.CoUninitialize();
            }
            CompleteAuxiliaryWorkers();
        }
    }

    private static unsafe bool TryInitializeMta() =>
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).Succeeded;

    private void CompleteAuxiliaryWorkers()
    {
        if (State != AudioMonitorState.Faulted) ChangeState(AudioMonitorState.Off);
        if (TryStopControlEventPublisher())
            auxiliaryWorkersSettled.TrySetResult();
        else
            _ = CompleteAfterControlPublisherSettlesAsync();
    }

    private async Task CompleteAfterControlPublisherSettlesAsync()
    {
        try { await controlEventPublisher.Completion.ConfigureAwait(false); }
        catch (Exception exception)
        {
            SafeWarning($"Audio control-event-publisher completion failed safely ({exception.GetType().Name}).");
        }
        finally
        {
            auxiliaryWorkersSettled.TrySetResult();
        }
    }

    private void WorkerEntry()
    {
        try { WorkerMain(); }
        catch (Exception exception)
        {
            SafeWarning($"The audio packet worker ended safely after an unexpected {exception.GetType().Name}.");
        }
        finally
        {
            packetWorkerSettled.TrySetResult();
        }
    }

    private async Task CompleteWhenAllWorkersSettleAsync()
    {
        try
        {
            await Task.WhenAll(packetWorkerSettled.Task, auxiliaryWorkersSettled.Task).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private void SafeInformation(string message)
    {
        try { logger.Information(message); }
        catch (Exception) { }
    }

    private void SafeWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }

}

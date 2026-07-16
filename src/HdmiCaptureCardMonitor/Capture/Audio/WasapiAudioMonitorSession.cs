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
    private const int DefaultTargetQueuePeriods = 2;
    private const uint SupervisorPollMilliseconds = 100;

    private readonly AudioMonitorStartRequest request;
    private readonly IApplicationLogger logger;
    private readonly AudioGainController gain;
    private readonly bool preferAudioClient3;
    private readonly int targetQueuePeriods;
    private readonly bool enableRateAdjustment;
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
        int targetQueuePeriods = DefaultTargetQueuePeriods,
        bool enableRateAdjustment = true)
    {
        this.request = request;
        this.logger = logger;
        this.preferAudioClient3 = preferAudioClient3;
        if (targetQueuePeriods is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(targetQueuePeriods), "The queue target must be two or three periods.");
        this.targetQueuePeriods = targetQueuePeriods;
        this.enableRateAdjustment = enableRateAdjustment;
        gain = new AudioGainController();
        gain.SetVolume(request.InitialVolumePercent);
        gain.SetMuted(request.InitiallyMuted);
        SessionId = Guid.NewGuid();
        worker = new Thread(WorkerEntry)
        {
            IsBackground = true,
            Name = $"Audio session supervisor {SessionId:N}",
            Priority = ThreadPriority.Normal
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
        AudioCapturePacketWorker? captureWorker = null;
        AudioRenderPacketWorker? renderWorker = null;
        AudioQueueRateController? rateController = null;
        WAVEFORMATEX* renderMix = null;
        WAVEFORMATEX* captureMix = null;
        SafeFileHandle? captureEvent = null;
        SafeFileHandle? renderEvent = null;
        SafeFileHandle? localStopEvent = null;
        var comInitialized = false;
        var captureStarted = false;
        var renderStarted = false;
        try
        {
            var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartment.Failed) Marshal.ThrowExceptionForHR(apartment.Value);
            comInitialized = true;

            localStopEvent = PInvoke.CreateEvent(null, true, Volatile.Read(ref stopRequested) != 0, null);
            captureEvent = PInvoke.CreateEvent(null, false, false, null);
            renderEvent = PInvoke.CreateEvent(null, false, false, null);
            if (localStopEvent.IsInvalid || captureEvent.IsInvalid || renderEvent.IsInvalid)
                throw new AudioSessionException(AudioMonitorFailureCategory.EndpointCreateFailed,
                    "Windows could not create audio synchronization events.");
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

            var stablePeriod = Math.Max(Math.Max(capturePeriod, renderPeriod), 1);
            var capacityFrames = checked(stablePeriod * BufferPeriodCapacity);
            var startupTarget = checked(stablePeriod * targetQueuePeriods);
            var highWatermark = Math.Min(capacityFrames, checked(startupTarget + stablePeriod * 2));
            var ring = new SpscAudioFrameBuffer(capacityFrames, managedFormat.ChannelCount, highWatermark);
            var discontinuityTracker = new AudioDiscontinuityTracker();
            captureWorker = new AudioCapturePacketWorker(
                localStopEvent,
                captureEvent,
                captureBufferAccess,
                ring,
                managedFormat,
                startupTarget,
                captureBufferFrames,
                managedFormat.FramesToMilliseconds(capturePeriod),
                discontinuityTracker,
                () => (rateController?.RequestedAdjustmentPpm, rateController?.AppliedAdjustmentPpm));
            renderWorker = new AudioRenderPacketWorker(
                localStopEvent,
                renderEvent,
                renderBufferAccess,
                ring,
                managedFormat,
                gain,
                renderBufferFramesNative,
                renderPeriod,
                managedFormat.FramesToMilliseconds(renderPeriod),
                discontinuityTracker);

            var captureReady = captureWorker.StartAndWaitReady(TimeSpan.FromSeconds(2));
            var renderReady = renderWorker.StartAndWaitReady(TimeSpan.FromSeconds(2));
            if (!captureReady || !renderReady)
                throw new AudioSessionException(AudioMonitorFailureCategory.EndpointCreateFailed,
                    "Windows could not start the independent audio packet workers.");

            captureClient.Start();
            captureStarted = true;
            while (!captureWorker.TargetReached.WaitOne(TimeSpan.FromMilliseconds(50)))
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
                    return;
                }
                ThrowWorkerFailure(captureWorker, renderWorker);
            }

            var startupSilenceFrames = renderBufferFrames;
            AudioNativeBufferProcessor.PrefillRenderWithSilence(
                renderBufferAccess, managedFormat.ChannelCount, renderBufferFramesNative);
            renderClient.Start();
            renderStarted = true;
            captureWorker.CompletePreroll();
            if (enableRateAdjustment)
            {
                rateController = new AudioQueueRateController(
                    renderClient, logger, managedFormat.SampleRate, startupTarget, stablePeriod);
                if (!rateController.Start())
                    throw new AudioSessionException(AudioMonitorFailureCategory.UnsupportedAudioFormat,
                        "The selected output does not support evidence-driven shared-mode rate adjustment.");
            }

            captureWorker.SetPhase(AudioDiscontinuityPhase.Transition);
            var transitionClock = Stopwatch.StartNew();
            ChangeState(gain.IsMuted ? AudioMonitorState.Muted : AudioMonitorState.Monitoring);
            startup.TrySetResult(AudioMonitorStartResult.Started(SessionId));
            SafeInformation($"Audio monitoring started for {request.CaptureEndpoint.DisplayName} to " +
                $"{request.RenderEndpoint?.DisplayName ?? "System default output"}; {managedFormat}; {path}; " +
                $"dedicated capture/render workers; native render buffer {renderBufferFrames} frames; " +
                $"ring target/capacity {startupTarget}/{capacityFrames} frames.");

            var diagnosticsClock = Stopwatch.StartNew();
            var lastDiagnosticsTimestamp = Stopwatch.GetTimestamp();
            ulong previousDevicePosition = 0;
            long previousRenderFrames = 0;
            var supervisorHandles = new[] { ToHandle(localStopEvent) };
            while (Volatile.Read(ref stopRequested) == 0)
            {
                var wait = PInvoke.WaitForMultipleObjects(
                    supervisorHandles, false, SupervisorPollMilliseconds);
                if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
                ThrowWorkerFailure(captureWorker, renderWorker);
                if (transitionClock.Elapsed >= TimeSpan.FromSeconds(2))
                    captureWorker.SetPhase(AudioDiscontinuityPhase.SteadyState);

                var ringSnapshot = ring.Snapshot();
                var captureSnapshot = captureWorker.Snapshot();
                var renderSnapshot = renderWorker.Snapshot();
                rateController?.UpdateObservation(
                    ringSnapshot.QueuedFrames,
                    captureSnapshot.FramesCaptured,
                    renderSnapshot.FramesRequested,
                    captureSnapshot.DiscontinuityCount,
                    renderSnapshot.LateWakeCount);
                if (rateController is not null && !rateController.IsAvailable)
                    throw new AudioSessionException(AudioMonitorFailureCategory.AudioProcessingFailure,
                        "Windows audio rate adjustment became unavailable.");

                if (diagnosticsClock.ElapsedMilliseconds < 500) continue;
                diagnosticsClock.Restart();
                var now = Stopwatch.GetTimestamp();
                var seconds = (now - lastDiagnosticsTimestamp) / (double)Stopwatch.Frequency;
                var captureFps = seconds <= 0 || previousDevicePosition == 0 ||
                    captureSnapshot.LastDevicePosition < previousDevicePosition
                        ? 0
                        : (captureSnapshot.LastDevicePosition - previousDevicePosition) / seconds;
                var renderFps = seconds <= 0
                    ? 0
                    : (renderSnapshot.FramesRequested - previousRenderFrames) / seconds;
                lastDiagnosticsTimestamp = now;
                previousDevicePosition = captureSnapshot.LastDevicePosition;
                previousRenderFrames = renderSnapshot.FramesRequested;
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
                    ringSnapshot.QueuedFrames,
                    ringSnapshot.MaximumQueuedFrames,
                    captureSnapshot.FramesCaptured,
                    renderSnapshot.FramesRequested,
                    startupSilenceFrames + renderSnapshot.SilenceInsertedFrames,
                    captureSnapshot.DiscontinuityCount,
                    captureSnapshot.TimestampErrorCount,
                    ringSnapshot.StarvationEvents,
                    ringSnapshot.PhysicalCapacityEvents,
                    ringSnapshot.PhysicalCapacityDroppedFrames + ringSnapshot.LatencyTrimmedFrames,
                    gain.VolumePercent,
                    gain.IsMuted,
                    captureSnapshot.LastPacketTime,
                    renderSnapshot.LastRenderTime,
                    null,
                    rateController?.AppliedAdjustmentPpm,
                    LastCaptureDevicePosition: captureSnapshot.LastDevicePosition,
                    LastCaptureQpcPosition: captureSnapshot.LastQpcPosition,
                    MmcssRegistered: captureSnapshot.MmcssRegistered && renderSnapshot.MmcssRegistered,
                    RingBufferCapacityFrames: capacityFrames,
                    TargetQueueFrames: startupTarget,
                    AverageQueueFrames: ringSnapshot.AverageQueuedFrames,
                    RequestedRateAdjustment: rateController?.RequestedAdjustmentPpm,
                    RateAdjustmentSaturationMilliseconds: checked((long)(rateController?.SaturationDuration.TotalMilliseconds ?? 0)),
                    RateAdjustmentDirectionChangeCount: rateController?.DirectionChangeCount ?? 0,
                    DiscontinuityTimeline: discontinuityTracker.Snapshot(),
                    PhysicalCapacityDroppedFrames: ringSnapshot.PhysicalCapacityDroppedFrames,
                    LatencyTrimmedFrames: ringSnapshot.LatencyTrimmedFrames,
                    RingStarvationEvents: ringSnapshot.StarvationEvents,
                    NativeRenderUnderfillEvents: renderSnapshot.NativeUnderfillEvents,
                    StartupSilenceFrames: startupSilenceFrames,
                    CaptureEventIntervalAverageMilliseconds: captureSnapshot.EventTiming.AverageMilliseconds,
                    CaptureEventIntervalP95Milliseconds: captureSnapshot.EventTiming.P95Milliseconds,
                    CaptureEventIntervalMaximumMilliseconds: captureSnapshot.EventTiming.MaximumMilliseconds,
                    CaptureLongGapCount: captureSnapshot.EventTiming.LongGapCount,
                    EmptyCaptureWakeCount: captureSnapshot.EmptyWakeCount,
                    CapturePacketFrameDistribution: captureSnapshot.PacketFrameDistribution,
                    RenderEventIntervalAverageMilliseconds: renderSnapshot.EventTiming.AverageMilliseconds,
                    RenderEventIntervalP95Milliseconds: renderSnapshot.EventTiming.P95Milliseconds,
                    RenderEventIntervalMaximumMilliseconds: renderSnapshot.EventTiming.MaximumMilliseconds,
                    RenderLongGapCount: renderSnapshot.EventTiming.LongGapCount,
                    CurrentRenderPaddingFrames: renderSnapshot.CurrentPaddingFrames,
                    RenderFramesAvailableAverage: renderSnapshot.AverageFramesAvailable,
                    RenderFramesAvailableMaximum: renderSnapshot.MaximumFramesAvailable,
                    RenderLateWakeCount: renderSnapshot.LateWakeCount,
                    RenderFramesAvailableDistribution: renderSnapshot.FramesAvailableDistribution,
                    EstimatedClockDriftPpm: rateController?.EstimatedClockDriftPpm,
                    RateAdjustmentActive: rateController?.IsAdjustmentActive ?? false,
                    RateAdjustmentActivationMilliseconds: checked((long)(rateController?.ActivationDuration.TotalMilliseconds ?? 0)),
                    CaptureDeviceFramesPerSecond: captureFps,
                    RenderConsumptionFramesPerSecond: renderFps,
                    TotalKnownBufferedFrames: checked(startupTarget + renderBufferFrames))));
            }
        }
        catch (Exception exception) when (exception is COMException or AudioSessionException or OverflowException)
        {
            var failure = CreateFailure(exception);
            ChangeState(AudioMonitorState.Faulted);
            startup.TrySetResult(AudioMonitorStartResult.Failed(SessionId, failure));
            _ = controlEventPublisher.PublishFailure(new AudioMonitorFailureEventArgs(SessionId, failure));
            SafeWarning($"Audio monitoring failed safely with {failure.Category}" +
                (failure.HResult is int hresult ? $" (0x{hresult:X8})." : "."));
        }
        finally
        {
            startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
            try
            {
                RequestStop();
                WaitForPacketWorker(captureWorker);
                WaitForPacketWorker(renderWorker);
                if (rateController is not null)
                {
                    _ = TryStopRateController(rateController);
                    WaitForCompletion(rateController.Completion, "queue-rate controller completion");
                }
                _ = TryStopDiagnosticsPublisher();
                WaitForCompletion(diagnosticsPublisher.Completion, "diagnostics-publisher completion");
                TryCleanup(() => { if (captureStarted) captureClient?.Stop(); }, "capture stop");
                TryCleanup(() => { if (renderStarted) renderClient?.Stop(); }, "render stop");
                TryCleanup(() => ReleaseComObject(captureService), "capture-service release");
                TryCleanup(() => ReleaseComObject(renderService), "render-service release");
                TryCleanup(() => ReleaseComObject(captureClient), "capture-client release");
                TryCleanup(() => ReleaseComObject(renderClient), "render-client release");
                TryCleanup(() => ReleaseComObject(captureDevice), "capture-endpoint release");
                TryCleanup(() => ReleaseComObject(renderDevice), "render-endpoint release");
                TryCleanup(() => ReleaseComObject(enumerator), "endpoint-enumerator release");
                TryCleanup(() => captureWorker?.Dispose(), "capture-worker handle disposal");
                TryCleanup(() => renderWorker?.Dispose(), "render-worker handle disposal");
                Volatile.Write(ref stopEvent, null);
                TryCleanup(() => captureEvent?.Dispose(), "capture-event disposal");
                TryCleanup(() => renderEvent?.Dispose(), "render-event disposal");
                TryCleanup(() => localStopEvent?.Dispose(), "stop-event disposal");
                TryCleanup(() => { if (captureMix is not null) PInvoke.CoTaskMemFree(captureMix); }, "capture-format release");
                TryCleanup(() => { if (renderMix is not null) PInvoke.CoTaskMemFree(renderMix); }, "render-format release");
                TryCleanup(() => { if (comInitialized) PInvoke.CoUninitialize(); }, "COM uninitialization");
            }
            finally
            {
                CompleteAuxiliaryWorkers();
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

    private static unsafe string DescribeFormat(WAVEFORMATEX* format) => format is null
        ? "Unavailable"
        : $"{format->nSamplesPerSec} Hz, {format->nChannels} channels, {format->wBitsPerSample}-bit, tag 0x{format->wFormatTag:X4}";

    private static uint RoundDown(uint value, uint multiple) => multiple == 0 ? 0 : value / multiple * multiple;

    private static unsafe HANDLE ToHandle(SafeHandle handle) => new((void*)handle.DangerousGetHandle());

    private static void ThrowWorkerFailure(
        AudioCapturePacketWorker captureWorker,
        AudioRenderPacketWorker renderWorker)
    {
        var failure = captureWorker.Failure ?? renderWorker.Failure;
        if (failure is null)
        {
            if (!captureWorker.Completion.IsCompleted && !renderWorker.Completion.IsCompleted) return;
            throw new AudioSessionException(AudioMonitorFailureCategory.AudioProcessingFailure,
                "An audio packet worker stopped unexpectedly.");
        }
        if (failure is AudioSessionException audio) throw audio;
        if (failure is COMException com)
            throw new AudioSessionException(MapFailureCategory(com.HResult), com.Message, com);
        throw new AudioSessionException(AudioMonitorFailureCategory.AudioProcessingFailure,
            "An audio packet worker stopped safely after an unexpected failure.", failure);
    }

    private void WaitForPacketWorker(AudioPacketWorker? worker)
    {
        if (worker is null) return;
        WaitForCompletion(worker.Completion, "packet-worker completion");
    }

    private void WaitForCompletion(Task task, string operation)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            SafeWarning($"Audio {operation} failed safely ({exception.GetType().Name}).");
        }
    }

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

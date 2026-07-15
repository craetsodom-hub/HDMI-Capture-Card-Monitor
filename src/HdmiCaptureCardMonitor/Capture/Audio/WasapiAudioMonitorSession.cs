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
    private const int TargetQueuePeriods = 3;
    private const uint WaitMilliseconds = 1000;

    private readonly AudioMonitorStartRequest request;
    private readonly IApplicationLogger logger;
    private readonly AudioGainController gain;
    private readonly bool preferAudioClient3;
    private readonly Thread worker;
    private readonly TaskCompletionSource<AudioMonitorStartResult> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SafeFileHandle? stopEvent;
    private int state = (int)AudioMonitorState.Off;
    private int started;
    private int stopRequested;
    private int disposed;

    internal WasapiAudioMonitorSession(AudioMonitorStartRequest request, IApplicationLogger logger, bool preferAudioClient3 = true)
    {
        this.request = request;
        this.logger = logger;
        this.preferAudioClient3 = preferAudioClient3;
        gain = new AudioGainController();
        gain.SetVolume(request.InitialVolumePercent);
        gain.SetMuted(request.InitiallyMuted);
        SessionId = Guid.NewGuid();
        worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = $"Audio monitor {SessionId:N}",
            Priority = ThreadPriority.AboveNormal
        };
        worker.SetApartmentState(ApartmentState.MTA);
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
        ChangeState(AudioMonitorState.Starting);
        worker.Start();
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
        WAVEFORMATEX* renderMix = null;
        WAVEFORMATEX* captureMix = null;
        SafeFileHandle? captureEvent = null;
        SafeFileHandle? renderEvent = null;
        SafeFileHandle? localStopEvent = null;
        AvRevertMmThreadCharacteristicsSafeHandle? mmcss = null;
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
                throw new AudioSessionException(AudioMonitorFailureCategory.EndpointCreateFailed, "Windows could not create audio synchronization events.");
            Volatile.Write(ref stopEvent, localStopEvent);

            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDevice(request.CaptureEndpoint.Id, out captureDevice);
            if (request.RenderEndpoint is null)
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out renderDevice);
            else
                enumerator.GetDevice(request.RenderEndpoint.Id, out renderDevice);

            captureClient = ActivateAudioClient(captureDevice);
            renderClient = ActivateAudioClient(renderDevice);
            renderClient.GetMixFormat(out renderMix);
            captureClient.GetMixFormat(out captureMix);
            var common = CreateCommonFormat(renderMix);
            var managedFormat = AudioStreamFormat.CreateIeeeFloat(
                checked((int)common.Format.nSamplesPerSec), common.Format.nChannels, common.dwChannelMask);

            var path = preferAudioClient3 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) &&
                TryInitializeClient3Pair(captureClient, renderClient, &common, out var capturePeriod, out var renderPeriod)
                ? AudioMonitorInitializationPath.AudioClient3
                : InitializeClassicPair(captureClient, renderClient, &common, out capturePeriod, out renderPeriod);

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
            mmcss = TryRegisterMmcss();

            var stablePeriod = Math.Max(Math.Max(capturePeriod, renderPeriod), 1);
            var capacityFrames = checked(stablePeriod * BufferPeriodCapacity);
            var highWatermark = checked(stablePeriod * HighWatermarkPeriods);
            var startupTarget = Math.Min(checked(stablePeriod * TargetQueuePeriods), highWatermark);
            var ring = new AudioFrameRingBuffer(capacityFrames, managedFormat.ChannelCount, highWatermark);

            var prefillFrames = checked((uint)Math.Min(renderPeriod, renderBufferFrames));
            PrefillRenderWithSilence(renderClient, renderPacketService, managedFormat.ChannelCount, prefillFrames);
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
            var diagnosticsClock = Stopwatch.StartNew();
            var handles = new[] { ToHandle(localStopEvent), ToHandle(captureEvent), ToHandle(renderEvent) };

            while (ring.QueuedFrames < startupTarget && Volatile.Read(ref stopRequested) == 0)
            {
                var wait = PInvoke.WaitForMultipleObjects(handles.AsSpan(0, 2), false, WaitMilliseconds);
                if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
                if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1))
                    ProcessCapture(capturePacketService, ring, managedFormat, ref framesCaptured, ref silentFrames,
                        ref discontinuities, ref timestampErrors, ref lastDevicePosition, ref lastQpcPosition, ref lastPacket);
                else if (wait == WAIT_EVENT.WAIT_FAILED)
                    throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio event waiting failed.");
            }

            if (Volatile.Read(ref stopRequested) != 0)
            {
                startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
                return;
            }

            renderClient.Start();
            renderStarted = true;
            ChangeState(gain.IsMuted ? AudioMonitorState.Muted : AudioMonitorState.Monitoring);
            startup.TrySetResult(AudioMonitorStartResult.Started(SessionId));
            SafeInformation($"Audio monitoring started for {request.CaptureEndpoint.DisplayName} to " +
                $"{request.RenderEndpoint?.DisplayName ?? "System default output"}; {managedFormat}; {path}; " +
                $"periods {capturePeriod}/{renderPeriod} frames; queue capacity {capacityFrames} frames.");

            while (Volatile.Read(ref stopRequested) == 0)
            {
                var wait = PInvoke.WaitForMultipleObjects(handles, false, WaitMilliseconds);
                if (wait == WAIT_EVENT.WAIT_OBJECT_0) break;
                if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1))
                    ProcessCapture(capturePacketService, ring, managedFormat, ref framesCaptured, ref silentFrames,
                        ref discontinuities, ref timestampErrors, ref lastDevicePosition, ref lastQpcPosition, ref lastPacket);
                else if (wait == (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 2))
                    ProcessRender(renderClient, renderPacketService, ring, managedFormat, gain, renderBufferFramesNative, renderPeriod,
                        ref framesRendered, ref silentFrames, ref lastRender);
                else if (wait == WAIT_EVENT.WAIT_FAILED)
                    throw new AudioSessionException(AudioMonitorFailureCategory.BufferFailure, "Audio event waiting failed.");

                if (diagnosticsClock.ElapsedMilliseconds >= 500)
                {
                    diagnosticsClock.Restart();
                    DiagnosticsUpdated?.Invoke(this, new AudioMonitorDiagnosticsEventArgs(SessionId, new AudioMonitorDiagnostics(
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
                        LastCaptureDevicePosition: lastDevicePosition,
                        LastCaptureQpcPosition: lastQpcPosition,
                        MmcssRegistered: mmcss is not null,
                        RingBufferCapacityFrames: capacityFrames,
                        TargetQueueFrames: startupTarget)));
                }
            }
        }
        catch (Exception exception) when (exception is COMException or AudioSessionException or OverflowException)
        {
            var failure = CreateFailure(exception);
            ChangeState(AudioMonitorState.Faulted);
            startup.TrySetResult(AudioMonitorStartResult.Failed(SessionId, failure));
            MonitoringFailed?.Invoke(this, new AudioMonitorFailureEventArgs(SessionId, failure));
            SafeWarning($"Audio monitoring failed safely with {failure.Category}" +
                (failure.HResult is int hresult ? $" (0x{hresult:X8})." : "."));
        }
        finally
        {
            startup.TrySetResult(AudioMonitorStartResult.Cancelled(SessionId));
            TryCleanup(() => { if (captureStarted) captureClient?.Stop(); }, "capture stop");
            TryCleanup(() => { if (renderStarted) renderClient?.Stop(); }, "render stop");
            TryCleanup(() => ReleaseComObject(captureService), "capture-service release");
            TryCleanup(() => ReleaseComObject(renderService), "render-service release");
            TryCleanup(() => ReleaseComObject(captureClient), "capture-client release");
            TryCleanup(() => ReleaseComObject(renderClient), "render-client release");
            TryCleanup(() => ReleaseComObject(captureDevice), "capture-endpoint release");
            TryCleanup(() => ReleaseComObject(renderDevice), "render-endpoint release");
            TryCleanup(() => ReleaseComObject(enumerator), "endpoint-enumerator release");
            Volatile.Write(ref stopEvent, null);
            captureEvent?.Dispose();
            renderEvent?.Dispose();
            localStopEvent?.Dispose();
            if (captureMix is not null) PInvoke.CoTaskMemFree(captureMix);
            if (renderMix is not null) PInvoke.CoTaskMemFree(renderMix);
            TryCleanup(() => mmcss?.Dispose(), "MMCSS revert");
            if (comInitialized) PInvoke.CoUninitialize();
            if (State != AudioMonitorState.Faulted) ChangeState(AudioMonitorState.Off);
            completion.TrySetResult();
        }
    }

    private static IAudioClient ActivateAudioClient(IMMDevice device)
    {
        device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out var value);
        return (IAudioClient)value;
    }

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
    private static unsafe bool TryInitializeClient3Pair(
        IAudioClient capture,
        IAudioClient render,
        WAVEFORMATEXTENSIBLE* format,
        out int capturePeriod,
        out int renderPeriod)
    {
        capturePeriod = 0;
        renderPeriod = 0;
        if (capture is not IAudioClient3 capture3 || render is not IAudioClient3 render3) return false;
        if (!IsExactlySupported(capture, &format->Format) || !IsExactlySupported(render, &format->Format)) return false;
        try
        {
            capture3.GetSharedModeEnginePeriod(&format->Format, out var captureDefault, out var captureFundamental, out var captureMinimum, out _);
            render3.GetSharedModeEnginePeriod(&format->Format, out var renderDefault, out var renderFundamental, out var renderMinimum, out _);
            var commonFundamental = LeastCommonMultiple(captureFundamental, renderFundamental);
            var minimum = Math.Max(captureMinimum, renderMinimum);
            var maximum = Math.Min(captureDefault, renderDefault);
            var period = RoundDown(maximum, commonFundamental);
            if (period < minimum || period > maximum) return false;
            const uint flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            capture3.InitializeSharedAudioStream(flags, period, &format->Format, null);
            render3.InitializeSharedAudioStream(flags, period, &format->Format, null);
            capturePeriod = checked((int)period);
            renderPeriod = checked((int)period);
            return true;
        }
        catch (COMException)
        {
            throw new AudioSessionException(
                AudioMonitorFailureCategory.UnsupportedAudioFormat,
                "The low-latency shared audio period could not be initialized.");
        }
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
        var flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
            PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
            PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        capture.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, duration, 0, &format->Format, null);
        render.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, duration, 0, &format->Format, null);
        capture.GetBufferSize(out var captureFrames);
        render.GetBufferSize(out var renderFrames);
        capturePeriod = checked((int)Math.Max(1, captureFrames / 2));
        renderPeriod = checked((int)Math.Max(1, renderFrames / 2));
        return AudioMonitorInitializationPath.ClassicSharedFallback;
    }

    private static unsafe bool IsExactlySupported(IAudioClient client, WAVEFORMATEX* format)
    {
        WAVEFORMATEX* closest = null;
        try { return client.IsFormatSupported(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, format, &closest).Value == 0; }
        finally { if (closest is not null) PInvoke.CoTaskMemFree(closest); }
    }

    private static unsafe void PrefillRenderWithSilence(
        IAudioClient client,
        IAudioRenderClient service,
        int channelCount,
        uint bufferFrames)
    {
        if (bufferFrames == 0) return;
        service.GetBuffer(bufferFrames, out var data);
        new Span<float>(data, checked((int)bufferFrames * channelCount)).Clear();
        service.ReleaseBuffer(bufferFrames, 0);
        client.GetCurrentPadding(out _);
    }

    private static unsafe void ProcessCapture(
        IAudioCaptureClient service,
        AudioFrameRingBuffer ring,
        AudioStreamFormat format,
        ref long framesCaptured,
        ref long silentFrames,
        ref long discontinuities,
        ref long timestampErrors,
        ref ulong lastDevicePosition,
        ref ulong lastQpcPosition,
        ref DateTimeOffset? lastPacket)
    {
        service.GetNextPacketSize(out var packetFrames);
        while (packetFrames > 0)
        {
            service.GetBuffer(out var data, out var frames, out var rawFlags, out var devicePosition, out var qpcPosition);
            lastDevicePosition = devicePosition;
            lastQpcPosition = qpcPosition;
            var flags = (_AUDCLNT_BUFFERFLAGS)rawFlags;
            try
            {
                var count = checked((int)frames);
                var silent = (flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                var samples = new ReadOnlySpan<float>(data, format.FramesToSampleCount(count));
                ring.Write(samples, count, silent);
                framesCaptured += frames;
                if (silent) silentFrames += frames;
                if ((flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) discontinuities++;
                if ((flags & _AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) timestampErrors++;
                lastPacket = DateTimeOffset.UtcNow;
            }
            finally { service.ReleaseBuffer(frames); }
            service.GetNextPacketSize(out packetFrames);
        }
    }

    private static unsafe void ProcessRender(
        IAudioClient client,
        IAudioRenderClient service,
        AudioFrameRingBuffer ring,
        AudioStreamFormat format,
        AudioGainController gain,
        uint bufferFrames,
        int renderPeriodFrames,
        ref long framesRendered,
        ref long silentFrames,
        ref DateTimeOffset? lastRender)
    {
        client.GetCurrentPadding(out var padding);
        if (padding >= bufferFrames) return;
        var available = bufferFrames - padding;
        var period = checked((uint)Math.Max(1, renderPeriodFrames));
        if (available < period) return;
        available = period;
        service.GetBuffer(available, out var data);
        var frameCount = checked((int)available);
        var samples = new Span<float>(data, format.FramesToSampleCount(frameCount));
        var result = ring.Read(samples, frameCount);
        gain.Process(samples, frameCount, format.ChannelCount);
        service.ReleaseBuffer(available, 0);
        framesRendered += available;
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

    private static uint LeastCommonMultiple(uint left, uint right)
    {
        if (left == 0 || right == 0) return 0;
        return checked(left / GreatestCommonDivisor(left, right) * right);
    }

    private static uint GreatestCommonDivisor(uint left, uint right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
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
        if (previous != next) StateChanged?.Invoke(this, new AudioMonitorStateChangedEventArgs(SessionId, previous, next));
    }

    private static AudioMonitorFailure CreateFailure(Exception exception)
    {
        if (exception is AudioSessionException audio)
            return new AudioMonitorFailure(audio.Category, audio.Message, null, audio);
        var hresult = exception.HResult;
        var category = hresult switch
        {
            unchecked((int)0x80070005) => AudioMonitorFailureCategory.AccessDenied,
            unchecked((int)0x88890004) => AudioMonitorFailureCategory.DeviceInvalidated,
            unchecked((int)0x88890008) => AudioMonitorFailureCategory.UnsupportedAudioFormat,
            unchecked((int)0x8889000A) => AudioMonitorFailureCategory.DeviceInUse,
            unchecked((int)0x88890010) => AudioMonitorFailureCategory.AudioServiceNotRunning,
            unchecked((int)0x88890026) => AudioMonitorFailureCategory.ResourcesInvalidated,
            _ => AudioMonitorFailureCategory.AudioProcessingFailure
        };
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

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private void TryCleanup(Action action, string operation)
    {
        try { action(); }
        catch (COMException exception) { SafeWarning($"Audio {operation} cleanup reported 0x{exception.HResult:X8}; later cleanup continued."); }
        catch (ObjectDisposedException) { }
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

    private sealed class AudioSessionException(AudioMonitorFailureCategory category, string message) : Exception(message)
    {
        internal AudioMonitorFailureCategory Category { get; } = category;
    }
}

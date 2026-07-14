using System.Diagnostics;
using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Diagnostics;
using HdmiCaptureCardMonitor.Capture.Rendering;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed class MediaFoundationPreviewSession
{
    private static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);
    private readonly object syncRoot = new();
    private readonly PreviewStartRequest request;
    private readonly IApplicationLogger logger;
    private readonly CancellationTokenSource cancellation;
    private readonly TaskCompletionSource<PreviewStartResult> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PreviewDiagnosticsTracker diagnostics;
    private readonly Thread previewThread;
    private IMFSourceReader? publishedReader;
    private D3D11PreviewRenderer? activeRenderer;
    private Task<PreviewStopResult>? stopTask;
    private CancellationTokenRegistration requestCancellationRegistration;
    private bool registrationCreated;
    private bool readerReleased;
    private PreviewFailure? asynchronousFailure;
    private long sessionStartedTimestamp;
    private long lastSampleTimestamp;
    private long lastPresentedTimestamp;
    private int surfacePresentable;
    private int firstFramePresented;
    private int finished;
    private int stopping;

    public MediaFoundationPreviewSession(PreviewStartRequest request, IApplicationLogger logger)
    {
        this.request = request;
        this.logger = logger;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        diagnostics = new PreviewDiagnosticsTracker(request.Device.DisplayName, request.NativeFormat.DisplayLabel);
        SessionId = Guid.NewGuid();
        previewThread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"HDMI preview {SessionId:N}"
        };
        previewThread.SetApartmentState(ApartmentState.MTA);
    }

    public Guid SessionId { get; }
    public bool IsCompleted => completion.Task.IsCompleted;
    public Task Completion => completion.Task;

    public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented;
    public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated;
    public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;

    public async Task<PreviewStartResult> StartAsync()
    {
        requestCancellationRegistration = request.CancellationToken.Register(RequestStopWithoutWaiting);
        registrationCreated = true;
        previewThread.Start();
        return await startup.Task.ConfigureAwait(false);
    }

    public Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            if (IsCompleted) return Task.FromResult(PreviewStopResult.Stopped);
            return stopTask ??= StopCoreAsync(cancellationToken);
        }
    }

    private async Task<PreviewStopResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            var shouldFlush = false;
            lock (syncRoot)
            {
                if (!readerReleased)
                {
                    cancellation.Cancel();
                    shouldFlush = true;
                }
            }
            if (shouldFlush)
            {
                var flushTask = FlushReaderOnMtaControlThreadAsync();
                _ = flushTask.ContinueWith(
                    completed => logger.LogError("The MTA Source Reader flush control path failed unexpectedly.", completed.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        try
        {
            await completion.Task.WaitAsync(ShutdownBound, cancellationToken).ConfigureAwait(false);
            return PreviewStopResult.Stopped;
        }
        catch (TimeoutException exception)
        {
            var failure = new PreviewFailure(PreviewFailureCategory.ShutdownTimeout, "Preview shutdown exceeded the three-second safety bound.", null, exception);
            SafeLogWarning("Preview shutdown exceeded the three-second safety bound; native ownership remains with the preview thread.");
            return PreviewStopResult.Timeout(failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PreviewStopResult.Failed(new PreviewFailure(PreviewFailureCategory.Unknown, "Preview stop was cancelled."));
        }
    }

    private unsafe void Run()
    {
        IMFAttributes? activationAttributes = null;
        IMFAttributes? readerAttributes = null;
        IMFMediaSource? mediaSource = null;
        IMFSourceReader? reader = null;
        IMFSourceReaderEx? readerEx = null;
        IMFMediaType? nativeType = null;
        IMFMediaType? outputType = null;
        IMFMediaType? actualOutputType = null;
        D3D11PreviewRenderer? renderer = null;
        Timer? stallWatchdog = null;
        var apartmentInitialized = false;
        var surfaceSubscribed = false;

        try
        {
            sessionStartedTimestamp = Stopwatch.GetTimestamp();
            Volatile.Write(ref lastPresentedTimestamp, sessionStartedTimestamp);
            Volatile.Write(ref surfacePresentable, request.Surface.IsPresentable ? 1 : 0);
            if (!request.Surface.IsAvailable || !request.Surface.IsPresentable || request.Surface.PixelSize.IsEmpty)
                throw Failure(PreviewFailureCategory.D3DInitializationFailure, "The preview surface is unavailable or has no drawable area.");
            stallWatchdog = new Timer(CheckForPreviewStall, null, StallThreshold, TimeSpan.FromMilliseconds(500));
            var apartmentResult = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (apartmentResult.Failed) throw Failure(PreviewFailureCategory.Unknown, "The preview thread could not initialize COM.", apartmentResult.Value);
            apartmentInitialized = true;
            cancellation.Token.ThrowIfCancellationRequested();

            renderer = D3D11PreviewRenderer.Create(request.NativeFormat, request.Surface.PixelSize, request.Surface.Handle);
            Volatile.Write(ref activeRenderer, renderer);
            request.Surface.PixelSizeChanged += OnSurfaceSizeChanged;
            request.Surface.PresentabilityChanged += OnSurfacePresentabilityChanged;
            surfaceSubscribed = true;

            var createActivation = PInvoke.MFCreateAttributes(out activationAttributes, 2);
            if (createActivation.Failed || activationAttributes is null) throw Failure(PreviewFailureCategory.DeviceUnavailable, "The selected video device could not be prepared.", createActivation.Value);
            activationAttributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            activationAttributes.SetString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, request.Device.Id);

            var createSource = PInvoke.MFCreateDeviceSource(activationAttributes, out mediaSource);
            if (createSource.Failed || mediaSource is null) throw Failure(PreviewFailureMapper.Map(createSource.Value, PreviewFailureCategory.DeviceUnavailable), "The selected video device could not be opened.", createSource.Value);
            cancellation.Token.ThrowIfCancellationRequested();

            var createReaderAttributes = PInvoke.MFCreateAttributes(out readerAttributes, 4);
            if (createReaderAttributes.Failed || readerAttributes is null) throw Failure(PreviewFailureCategory.DeviceUnavailable, "The low-latency video reader could not be configured.", createReaderAttributes.Value);
            readerAttributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
            readerAttributes.SetUnknown(PInvoke.MF_SOURCE_READER_D3D_MANAGER, renderer.DeviceManager);
            readerAttributes.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            readerAttributes.SetUINT32(PInvoke.MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN, 1);

            var createReader = PInvoke.MFCreateSourceReaderFromMediaSource(mediaSource, readerAttributes, out reader);
            if (createReader.Failed || reader is null) throw Failure(PreviewFailureMapper.Map(createReader.Value, PreviewFailureCategory.DeviceUnavailable), "The selected video stream could not be opened.", createReader.Value);
            readerEx = reader as IMFSourceReaderEx ?? throw Failure(PreviewFailureCategory.UnsupportedPreviewFormat, "The selected device does not expose the required Source Reader interface.");

            var streamIndex = FirstVideoStream;
            reader.GetNativeMediaType(streamIndex, checked((uint)request.NativeFormat.NativeMediaTypeIndex), out nativeType);
            if (nativeType is null || !MatchesSelectedFormat(nativeType, request.NativeFormat))
            {
                throw Failure(PreviewFailureCategory.SelectedFormatUnavailable, "The selected native format is no longer available.");
            }

            // A current-media-type-changed flag is expected when this explicit selection
            // differs from the device default. SetNativeMediaType itself reports rejection.
            readerEx.SetNativeMediaType(streamIndex, nativeType, out _);

            var createOutputType = PInvoke.MFCreateMediaType(out outputType);
            if (createOutputType.Failed || outputType is null) throw Failure(PreviewFailureCategory.UnsupportedPreviewFormat, "A GPU-compatible preview output type could not be created.", createOutputType.Value);
            ConfigureNv12Output(outputType, request.NativeFormat);
            try
            {
                reader.SetCurrentMediaType(streamIndex, null, outputType);
            }
            catch (COMException exception)
            {
                throw Failure(PreviewFailureMapper.Map(exception.HResult, PreviewFailureCategory.DecoderUnavailable), "Windows could not negotiate decoded NV12 preview output.", exception.HResult, exception);
            }

            reader.GetCurrentMediaType(streamIndex, out actualOutputType);
            if (actualOutputType is null || !HasSubtype(actualOutputType, PInvoke.MFVideoFormat_NV12))
            {
                throw Failure(PreviewFailureCategory.UnsupportedPreviewFormat, "The selected format did not negotiate GPU-compatible NV12 output.");
            }

            var negotiatedOutput = ReadNegotiatedOutput(actualOutputType);
            diagnostics.SetNegotiation(
                request.NativeFormat.DisplayLabel,
                negotiatedOutput.Subtype,
                renderer.DriverType,
                negotiatedOutput.Width,
                negotiatedOutput.Height,
                negotiatedOutput.FrameRateNumerator,
                negotiatedOutput.FrameRateDenominator,
                negotiatedOutput.InterlaceMode);
            lock (syncRoot)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation.Token);
                publishedReader = reader;
            }

            startup.TrySetResult(PreviewStartResult.Started(SessionId));
            ReadAndRenderLoop(reader, renderer, streamIndex);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            startup.TrySetResult(PreviewStartResult.Cancelled(SessionId));
        }
        catch (PreviewNativeException exception)
        {
            logger.LogError($"Preview native failure: category={exception.Failure.Category}; hresult={(exception.Failure.HResult is int hresult ? $"0x{hresult:X8}" : "unavailable")}.", exception);
            diagnostics.RecordFailure(exception.Failure.Category);
            startup.TrySetResult(PreviewStartResult.Failed(SessionId, exception.Failure));
            if (ReferenceEquals(exception.Failure, Volatile.Read(ref asynchronousFailure)) || !cancellation.IsCancellationRequested)
                PreviewFailed?.Invoke(this, new PreviewFailureEventArgs(SessionId, exception.Failure));
        }
        catch (COMException exception)
        {
            var failure = new PreviewFailure(PreviewFailureMapper.Map(exception.HResult, PreviewFailureCategory.Unknown), "Windows could not continue the live preview.", exception.HResult, exception);
            logger.LogError($"Preview COM failure: category={failure.Category}; hresult=0x{exception.HResult:X8}.", exception);
            diagnostics.RecordFailure(failure.Category);
            startup.TrySetResult(PreviewStartResult.Failed(SessionId, failure));
            if (!cancellation.IsCancellationRequested) PreviewFailed?.Invoke(this, new PreviewFailureEventArgs(SessionId, failure));
        }
        catch (Exception exception)
        {
            var failure = new PreviewFailure(PreviewFailureCategory.Unknown, "The live preview stopped unexpectedly.", null, exception);
            logger.LogError("The native preview thread stopped unexpectedly.", exception);
            diagnostics.RecordFailure(failure.Category);
            startup.TrySetResult(PreviewStartResult.Failed(SessionId, failure));
            if (!cancellation.IsCancellationRequested) PreviewFailed?.Invoke(this, new PreviewFailureEventArgs(SessionId, failure));
        }
        finally
        {
            Volatile.Write(ref finished, 1);
            startup.TrySetResult(cancellation.IsCancellationRequested
                ? PreviewStartResult.Cancelled(SessionId)
                : PreviewStartResult.Failed(SessionId, new PreviewFailure(PreviewFailureCategory.Unknown, "Preview startup did not complete.")));

            var cleanupSteps = new List<PreviewCleanupStep>
            {
                new("stall-watchdog disposal", () => stallWatchdog?.Dispose()),
                new("surface event removal", () =>
                {
                    if (!surfaceSubscribed) return;
                    request.Surface.PixelSizeChanged -= OnSurfaceSizeChanged;
                    request.Surface.PresentabilityChanged -= OnSurfacePresentabilityChanged;
                }),
                new("cancellation-registration disposal", () =>
                {
                    if (registrationCreated) requestCancellationRegistration.Dispose();
                }),
                new("published Source Reader retirement", () =>
                {
                    Volatile.Write(ref activeRenderer, null);
                    lock (syncRoot)
                    {
                        publishedReader = null;
                        readerReleased = true;
                    }
                }),
                new("final diagnostics logging", LogFinalDiagnostics),
                new("actual output media-type release", () => ReleaseComObject(actualOutputType)),
                new("requested output media-type release", () => ReleaseComObject(outputType)),
                new("native media-type release", () => ReleaseComObject(nativeType)),
                new("Source Reader release", () => ReleaseComObject(reader)),
                new(
                    "media-source shutdown",
                    () => mediaSource?.Shutdown(),
                    exception => exception.HResult == MediaFoundationHResults.Shutdown),
                new("media-source release", () => ReleaseComObject(mediaSource)),
                new("reader attributes release", () => ReleaseComObject(readerAttributes)),
                new("activation attributes release", () => ReleaseComObject(activationAttributes)),
                new("renderer disposal", () => renderer?.Dispose()),
                new("COM apartment uninitialization", () =>
                {
                    if (apartmentInitialized) PInvoke.CoUninitialize();
                })
            };

            PreviewSessionCleanup.Execute(
                cleanupSteps,
                ReportCleanupFailure,
                cancellation.Dispose,
                () => completion.TrySetResult(true));
        }
    }

    private unsafe void ReadAndRenderLoop(IMFSourceReader reader, D3D11PreviewRenderer renderer, uint streamIndex)
    {
        while (!cancellation.IsCancellationRequested)
        {
            uint flags = 0;
            long timestamp = 0;
            IMFSample_unmanaged* sample = null;
            IMFMediaBuffer_unmanaged* buffer = null;
            IMFDXGIBuffer? dxgiBuffer = null;
            Stopwatch? sampleReturnToPresent = null;

            try
            {
                try
                {
                    reader.ReadSample(streamIndex, 0, null, &flags, &timestamp, &sample);
                    sampleReturnToPresent = Stopwatch.StartNew();
                }
                catch (COMException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (COMException exception)
                {
                    diagnostics.RecordReadFailure();
                    throw Failure(PreviewFailureMapper.Map(exception.HResult, PreviewFailureCategory.DeviceUnavailable), "The video device stopped delivering frames.", exception.HResult, exception);
                }

                HandleReaderFlags(flags);
                if (cancellation.IsCancellationRequested) break;
                if (sample is null)
                {
                    diagnostics.RecordNullSample();
                    PublishDiagnosticsIfDue();
                    continue;
                }

                var sampleArrivalTimestamp = Stopwatch.GetTimestamp();
                Volatile.Write(ref lastSampleTimestamp, sampleArrivalTimestamp);
                diagnostics.RecordReceived(timestamp, Stopwatch.GetElapsedTime(sessionStartedTimestamp, sampleArrivalTimestamp));
                if (Volatile.Read(ref surfacePresentable) == 0 || !request.Surface.IsPresentable)
                {
                    diagnostics.RecordSurfaceUnavailableSkip();
                    PublishDiagnosticsIfDue();
                    continue;
                }

                sample->GetBufferCount(out var bufferCount);
                if (bufferCount == 1) sample->GetBufferByIndex(0, out buffer);
                else sample->ConvertToContiguousBuffer(out buffer);
                if (buffer is null) throw Failure(PreviewFailureCategory.UnsupportedGpuBuffer, "The decoded frame did not contain a media buffer.");

                var queryResult = buffer->QueryInterface<IMFDXGIBuffer>(out dxgiBuffer);
                if (queryResult.Failed || dxgiBuffer is null)
                {
                    throw Failure(PreviewFailureCategory.UnsupportedGpuBuffer, "The decoded frame was not backed by a Direct3D texture.", queryResult.Value);
                }

                var processing = Stopwatch.StartNew();
                PreviewRenderOutcome renderOutcome;
                try { renderOutcome = renderer.Render(dxgiBuffer); }
                catch (PreviewNativeException exception) when (exception.Failure.Category is PreviewFailureCategory.PresentationFailure or PreviewFailureCategory.DeviceRemoved)
                {
                    diagnostics.RecordPresentationFailure();
                    throw;
                }

                if (renderOutcome == PreviewRenderOutcome.SurfaceUnavailable)
                {
                    diagnostics.RecordSurfaceUnavailableSkip();
                    PublishDiagnosticsIfDue();
                    continue;
                }
                if (renderOutcome == PreviewRenderOutcome.WasStillDrawing)
                {
                    diagnostics.RecordPresentWasStillDrawing();
                    PublishDiagnosticsIfDue();
                    continue;
                }
                processing.Stop();
                sampleReturnToPresent?.Stop();
                var presentedTimestamp = Stopwatch.GetTimestamp();
                var presentedAt = DateTimeOffset.UtcNow;
                Volatile.Write(ref lastPresentedTimestamp, presentedTimestamp);
                diagnostics.RecordRendered(
                    processing.Elapsed,
                    sampleReturnToPresent?.Elapsed ?? processing.Elapsed,
                    Stopwatch.GetElapsedTime(sessionStartedTimestamp, presentedTimestamp),
                    presentedAt);
                if (Interlocked.Exchange(ref firstFramePresented, 1) == 0)
                {
                    FirstFramePresented?.Invoke(this, new PreviewSessionEventArgs(SessionId));
                }
                PublishDiagnosticsIfDue();
            }
            finally
            {
                ReleaseComObject(dxgiBuffer);
                if (buffer is not null) buffer->Release();
                if (sample is not null) sample->Release();
            }
        }

        if (Volatile.Read(ref asynchronousFailure) is { } failure) throw new PreviewNativeException(failure);
    }

    private void CheckForPreviewStall(object? state)
    {
        _ = state;
        if (Volatile.Read(ref finished) != 0 || cancellation.IsCancellationRequested) return;
        var now = Stopwatch.GetTimestamp();
        var sampleTimestamp = Volatile.Read(ref lastSampleTimestamp);
        var presentationTimestamp = Volatile.Read(ref lastPresentedTimestamp);
        var firstFrameWasPresented = Volatile.Read(ref firstFramePresented) != 0;
        var category = PreviewWatchdogPolicy.Evaluate(
            Stopwatch.GetElapsedTime(sessionStartedTimestamp, now),
            sampleTimestamp == 0 ? null : Stopwatch.GetElapsedTime(sampleTimestamp, now),
            presentationTimestamp == 0 ? null : Stopwatch.GetElapsedTime(presentationTimestamp, now),
            Volatile.Read(ref surfacePresentable) != 0,
            firstFrameWasPresented,
            StallThreshold);
        if (category is null) return;

        var failure = category switch
        {
            PreviewFailureCategory.PreviewStalled => new PreviewFailure(category.Value, "The video input stopped delivering samples."),
            PreviewFailureCategory.PresentationFailure => new PreviewFailure(category.Value, "Video samples arrived, but the visible preview could not present them."),
            _ => new PreviewFailure(PreviewFailureCategory.StartupTimeout, "The video input did not deliver its first sample in time.")
        };
        if (Interlocked.CompareExchange(ref asynchronousFailure, failure, null) is not null) return;
        startup.TrySetResult(PreviewStartResult.Failed(SessionId, failure));
        SafeLogWarning(category == PreviewFailureCategory.PresentationFailure
            ? "The preview watchdog requested bounded shutdown because samples arrived but a presentable surface did not present for three seconds."
            : firstFrameWasPresented
                ? "The preview watchdog requested bounded shutdown after three seconds without a video sample."
                : "The preview startup watchdog requested bounded shutdown after three seconds without a first sample.");
        RequestStopWithoutWaiting();
    }

    private void HandleReaderFlags(uint flags)
    {
        var typedFlags = (MF_SOURCE_READER_FLAG)flags;
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
            throw Failure(PreviewFailureCategory.DeviceUnavailable, "The video reader reported a stream error.");
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED) != 0 ||
            (typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
            throw Failure(PreviewFailureCategory.MediaTypeChanged, "The video format changed while preview was active. Stop and select the format again.");
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            throw Failure(PreviewFailureCategory.EndOfStream, "The video input ended.");
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_STREAMTICK) != 0) diagnostics.RecordStreamTick();
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NEWSTREAM) != 0) logger.Debug("The Source Reader reported a new video stream notification.");
        if ((typedFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ALLEFFECTSREMOVED) != 0) logger.Warning("The Source Reader removed one or more video transforms.");
    }

    private void PublishDiagnosticsIfDue()
    {
        if (diagnostics.TryCreateThrottledSnapshot(DateTimeOffset.UtcNow, out var snapshot))
        {
            DiagnosticsUpdated?.Invoke(this, new PreviewDiagnosticsEventArgs(SessionId, snapshot));
        }
    }

    private void OnSurfaceSizeChanged(object? sender, PreviewSurfaceSize size)
    {
        _ = sender;
        Volatile.Write(ref surfacePresentable, request.Surface.IsPresentable && !size.IsEmpty ? 1 : 0);
        // This mailbox is capacity one; D3D resize work remains on the preview thread.
        Volatile.Read(ref activeRenderer)?.RequestResize(size);
    }

    private void OnSurfacePresentabilityChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        var presentable = request.Surface.IsPresentable && !request.Surface.PixelSize.IsEmpty;
        Volatile.Write(ref surfacePresentable, presentable ? 1 : 0);
        if (!presentable) return;

        // Restoration receives a fresh grace interval; this is not recorded as a
        // successful presentation, and a visible surface that still cannot present
        // will be diagnosed by the watchdog after the normal threshold.
        Volatile.Write(ref lastPresentedTimestamp, Stopwatch.GetTimestamp());
        Volatile.Read(ref activeRenderer)?.RequestResize(request.Surface.PixelSize);
    }

    private async Task FlushReaderOnMtaControlThreadAsync()
    {
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlThread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                var result = InitializeMta();
                initialized = result >= 0;
                if (result < 0)
                {
                    SafeLogWarning("The preview stop control path could not initialize COM for Source Reader flush.");
                    return;
                }

                lock (syncRoot)
                {
                    if (!readerReleased && publishedReader is not null)
                    {
                        try { publishedReader.Flush(FirstVideoStream); }
                        catch (COMException exception) { SafeLogWarning($"Source Reader flush failed safely with 0x{exception.HResult:X8}."); }
                    }
                }
            }
            finally
            {
                if (initialized) PInvoke.CoUninitialize();
                flushed.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = $"HDMI preview stop {SessionId:N}"
        };
        controlThread.SetApartmentState(ApartmentState.MTA);
        controlThread.Start();
        await flushed.Task.ConfigureAwait(false);
    }

    private void RequestStopWithoutWaiting()
    {
        var task = StopAsync();
        _ = task.ContinueWith(
            completed => SafeLogError("Preview cancellation stop failed unexpectedly.", completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogFinalDiagnostics()
    {
        var finalDiagnostics = diagnostics.CreateSnapshot(DateTimeOffset.UtcNow);
        logger.Information(
            $"Preview diagnostics: device='{finalDiagnostics.DeviceDisplayName}'; native='{finalDiagnostics.ActualNativeFormat}'; " +
            $"output={finalDiagnostics.NegotiatedOutputSubtype} {finalDiagnostics.ActualOutputWidth}x{finalDiagnostics.ActualOutputHeight} " +
            $"{finalDiagnostics.ActualOutputFrameRateNumerator}/{finalDiagnostics.ActualOutputFrameRateDenominator} {finalDiagnostics.ActualOutputInterlaceMode}; " +
            $"driver={finalDiagnostics.DriverType}; received={finalDiagnostics.FramesReceived}; rendered={finalDiagnostics.FramesRendered}; " +
            $"received-fps={finalDiagnostics.FramesReceivedPerSecond:F1}; rendered-fps={finalDiagnostics.RenderedFramesPerSecond:F1}; " +
            $"sample-timestamp-fps={finalDiagnostics.SampleTimestampFramesPerSecond:F1}; average-processing-ms={finalDiagnostics.AverageProcessingMilliseconds:F2}; " +
            $"p95-processing-ms={finalDiagnostics.P95ProcessingMilliseconds:F2}; average-return-to-present-ms={finalDiagnostics.AverageSampleReturnToPresentMilliseconds:F2}; " +
            $"p95-return-to-present-ms={finalDiagnostics.P95SampleReturnToPresentMilliseconds:F2}; surface-skips={finalDiagnostics.FramesSkippedForUnavailableSurface}; " +
            $"present-still-drawing={finalDiagnostics.PresentWasStillDrawingCount}; null-samples={finalDiagnostics.NullSamples}; stream-ticks={finalDiagnostics.StreamTicks}; " +
            $"presentation-failures={finalDiagnostics.PresentationFailures}; last-failure={finalDiagnostics.LastFailureCategory?.ToString() ?? "none"}.");
    }

    private void ReportCleanupFailure(string step, Exception exception)
    {
        try
        {
            logger.Warning($"Preview cleanup step '{step}' failed safely: {exception.GetType().Name}.");
        }
        catch (Exception) { }
    }

    private void SafeLogWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }

    private void SafeLogError(string message, Exception? exception)
    {
        try { logger.LogError(message, exception); }
        catch (Exception) { }
    }

    private static unsafe int InitializeMta() => PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).Value;

    private static uint FirstVideoStream => unchecked((uint)(int)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);

    private static bool MatchesSelectedFormat(IMFMediaType mediaType, NativeVideoCapability selected)
    {
        try
        {
            mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
            mediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var frameSize);
            mediaType.GetUINT64(PInvoke.MF_MT_FRAME_RATE, out var frameRate);
            var width = (uint)(frameSize >> 32);
            var height = (uint)frameSize;
            var numerator = (uint)(frameRate >> 32);
            var denominator = (uint)frameRate;
            if (subtype != selected.MediaSubtype || width != selected.Width || height != selected.Height || numerator != selected.FrameRateNumerator || denominator != selected.FrameRateDenominator) return false;

            if (selected.InterlaceMode == VideoInterlaceMode.Unknown) return true;
            try
            {
                mediaType.GetUINT32(PInvoke.MF_MT_INTERLACE_MODE, out var interlace);
                var actual = interlace switch
                {
                    2 => VideoInterlaceMode.Progressive,
                    3 or 4 or 5 or 6 => VideoInterlaceMode.Interlaced,
                    7 => VideoInterlaceMode.Mixed,
                    _ => VideoInterlaceMode.Unknown
                };
                return actual == selected.InterlaceMode;
            }
            catch (COMException) { return true; }
        }
        catch (COMException) { return false; }
    }

    private static void ConfigureNv12Output(IMFMediaType mediaType, NativeVideoCapability selected)
    {
        mediaType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        mediaType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        mediaType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, ((ulong)selected.Width << 32) | selected.Height);
        mediaType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, ((ulong)selected.FrameRateNumerator << 32) | selected.FrameRateDenominator);
        mediaType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, ((ulong)Math.Max(1, selected.PixelAspectRatioNumerator) << 32) | Math.Max(1, selected.PixelAspectRatioDenominator));
        mediaType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, selected.InterlaceMode == VideoInterlaceMode.Progressive ? 2u : 3u);
    }

    private static bool HasSubtype(IMFMediaType mediaType, Guid expected)
    {
        try { mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype); return subtype == expected; }
        catch (COMException) { return false; }
    }

    private static NegotiatedOutputFormat ReadNegotiatedOutput(IMFMediaType mediaType)
    {
        mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
        mediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var frameSize);
        mediaType.GetUINT64(PInvoke.MF_MT_FRAME_RATE, out var frameRate);
        var interlace = VideoInterlaceMode.Unknown;
        try
        {
            mediaType.GetUINT32(PInvoke.MF_MT_INTERLACE_MODE, out var interlaceValue);
            interlace = ToInterlaceMode(interlaceValue);
        }
        catch (COMException) { }

        return new NegotiatedOutputFormat(
            subtype == PInvoke.MFVideoFormat_NV12 ? "NV12" : subtype.ToString("D"),
            (uint)(frameSize >> 32),
            (uint)frameSize,
            (uint)(frameRate >> 32),
            (uint)frameRate,
            interlace);
    }

    private static VideoInterlaceMode ToInterlaceMode(uint value) => value switch
    {
        2 => VideoInterlaceMode.Progressive,
        3 or 4 or 5 or 6 => VideoInterlaceMode.Interlaced,
        7 => VideoInterlaceMode.Mixed,
        _ => VideoInterlaceMode.Unknown
    };

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static PreviewNativeException Failure(PreviewFailureCategory category, string message, int? hresult = null, Exception? exception = null) =>
        new(new PreviewFailure(category, message, hresult, exception));

    private readonly record struct NegotiatedOutputFormat(
        string Subtype,
        uint Width,
        uint Height,
        uint FrameRateNumerator,
        uint FrameRateDenominator,
        VideoInterlaceMode InterlaceMode);
}

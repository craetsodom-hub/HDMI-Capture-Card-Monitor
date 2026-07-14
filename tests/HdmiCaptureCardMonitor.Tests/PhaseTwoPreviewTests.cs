using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Diagnostics;
using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Capture.Rendering;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseTwoPreviewTests
{
    private static readonly CaptureDevice Camera = new("opaque-camera-link", "HD Camera", "HD Camera", true, CaptureBackend.MediaFoundation);
    private static readonly NativeVideoCapability Format = new(0, 0, 1280, 720, 30, 1, 30, Guid.NewGuid(), "NV12", VideoInterlaceMode.Progressive, 1, 1, "1280 × 720p · 30 fps · NV12");

    [Theory]
    [InlineData(1920, 1080, 1280, 720, 0, 0, 1280, 720)]
    [InlineData(1920, 1080, 1000, 1000, 0, 219, 1000, 562)]
    [InlineData(720, 1280, 1000, 500, 359, 0, 281, 500)]
    public void FitRectanglePreservesAspectRatio(int sw, int sh, int tw, int th, int x, int y, int width, int height)
    {
        Assert.Equal(new PixelRectangle(x, y, width, height), FitRectangleCalculator.Calculate(sw, sh, tw, th));
    }

    [Fact]
    public void FitRectangleReturnsEmptyForZeroSurface() =>
        Assert.True(FitRectangleCalculator.Calculate(1920, 1080, 0, 720).IsEmpty);

    [Theory]
    [InlineData(800, 600, 1, 1, 800, 600)]
    [InlineData(800, 600, 1.5, 1.5, 1200, 900)]
    [InlineData(0, 600, 1.5, 1.5, 0, 0)]
    public void DpiConversionUsesPixelDimensions(double width, double height, double sx, double sy, int expectedWidth, int expectedHeight) =>
        Assert.Equal(new PreviewSurfaceSize(expectedWidth, expectedHeight), DpiPixelSizeConverter.Convert(width, height, sx, sy));

    [Fact]
    public void ResizeMailboxCoalescesToOneLatestRequest()
    {
        var mailbox = new ResizeRequestMailbox();
        mailbox.Post(new PreviewSurfaceSize(640, 480));
        mailbox.Post(new PreviewSurfaceSize(1920, 1080));
        Assert.Equal(1, ResizeRequestMailbox.Capacity);
        Assert.True(mailbox.TryTake(out var size));
        Assert.Equal(new PreviewSurfaceSize(1920, 1080), size);
        Assert.False(mailbox.TryTake(out _));
    }

    [Fact]
    public void DiagnosticsCalculateFpsAverageAndP95WithBoundedTimingStorage()
    {
        var tracker = new PreviewDiagnosticsTracker("Camera", Format.DisplayLabel);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 300; index++)
        {
            tracker.RecordRendered(TimeSpan.FromMilliseconds(index % 10 + 1), start.AddMilliseconds(index * 20));
        }

        var snapshot = tracker.CreateSnapshot(start.AddSeconds(6));
        Assert.Equal(256, PreviewDiagnosticsTracker.TimingSampleCapacity);
        Assert.InRange(snapshot.RenderedFramesPerSecond, 49.9, 50.1);
        Assert.InRange(snapshot.AverageProcessingMilliseconds, 5.4, 5.7);
        Assert.InRange(snapshot.P95ProcessingMilliseconds, 9, 10);
    }

    [Fact]
    public void DiagnosticPublicationIsThrottledToTwicePerSecond()
    {
        var tracker = new PreviewDiagnosticsTracker("Camera", Format.DisplayLabel);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(tracker.TryCreateThrottledSnapshot(now, out _));
        Assert.False(tracker.TryCreateThrottledSnapshot(now.AddMilliseconds(499), out _));
        Assert.True(tracker.TryCreateThrottledSnapshot(now.AddMilliseconds(500), out _));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), PreviewFailureCategory.AccessDenied)]
    [InlineData(unchecked((int)0x80070020), PreviewFailureCategory.DeviceBusy)]
    [InlineData(unchecked((int)0x887A0005), PreviewFailureCategory.DeviceRemoved)]
    [InlineData(unchecked((int)0x887A0007), PreviewFailureCategory.DeviceRemoved)]
    [InlineData(unchecked((int)0xC00D5212), PreviewFailureCategory.DecoderUnavailable)]
    [InlineData(unchecked((int)0xC00D36B4), PreviewFailureCategory.UnsupportedPreviewFormat)]
    [InlineData(unchecked((int)0x80004005), PreviewFailureCategory.DeviceUnavailable)]
    public void NativeFailuresMapToTypedCategories(int hresult, PreviewFailureCategory expected) =>
        Assert.Equal(expected, PreviewFailureMapper.Map(hresult, PreviewFailureCategory.DeviceUnavailable));

    [Fact]
    public void PreviewHasNoApplicationFrameQueue() => Assert.Equal(0, D3D11PreviewRenderer.ApplicationFrameQueueCapacity);

    [Fact]
    public async Task EligibilityRequiresDeviceFormatReadyStateAndSurface()
    {
        var (viewModel, _, surface) = Create();
        Assert.False(viewModel.CanStartCapture);
        await MakeReadyAsync(viewModel);
        Assert.True(surface.IsAvailable);
        Assert.True(viewModel.CanStartCapture);
    }

    [Fact]
    public async Task MissingFormatAndScanningDisableStart()
    {
        var (viewModel, _, _) = Create();
        viewModel.Devices.Add(Camera);
        Assert.False(viewModel.CanStartCapture);
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        Assert.False(viewModel.CanStartCapture);
        await refresh;
    }

    [Fact]
    public async Task SuccessfulStartWaitsForFirstFrameBeforePreviewing()
    {
        var (viewModel, preview, surface) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        Assert.Equal(CaptureSessionState.Starting, viewModel.SessionState);
        Assert.True(viewModel.IsPreviewMessageVisible);
        Assert.False(surface.IsVideoVisible);
        Assert.True(surface.IsSurfaceActive);
        preview.CompleteStart();
        await start;
        Assert.Equal(CaptureSessionState.Starting, viewModel.SessionState);
        preview.RaiseFirstFrame();
        Assert.Equal(CaptureSessionState.Previewing, viewModel.SessionState);
        Assert.False(viewModel.IsPreviewMessageVisible);
        Assert.True(surface.IsVideoVisible);
    }

    [Fact]
    public async Task StopReturnsToReadyAndRetainsSelections()
    {
        var (viewModel, preview, surface) = Create();
        await StartAndPresentAsync(viewModel, preview);
        var stop = viewModel.StopPreviewForTestingAsync();
        Assert.Equal(CaptureSessionState.Stopping, viewModel.SessionState);
        preview.CompleteStop();
        await stop;
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);
        Assert.Equal(Camera, viewModel.SelectedDevice);
        Assert.Equal(Format, viewModel.SelectedFormat);
        Assert.False(surface.IsVideoVisible);
        Assert.False(surface.IsSurfaceActive);
        Assert.Equal("Preview stopped", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task StartFailureFaultsAndKeepsSurfaceHidden()
    {
        var (viewModel, preview, surface) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        preview.CompleteStartFailure(PreviewFailureCategory.DecoderUnavailable);
        preview.CompleteStop();
        await start;
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
        Assert.False(surface.IsVideoVisible);
        Assert.False(surface.IsSurfaceActive);
        Assert.Contains("decode", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledStartReturnsToReadyWithoutFaulting()
    {
        var (viewModel, preview, surface) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        preview.CompleteStartCancelled();
        await start;
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);
        Assert.False(surface.IsVideoVisible);
        Assert.Equal("Preview stopped", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task RuntimeFailureFaultsAndHidesSurface()
    {
        var (viewModel, preview, surface) = Create();
        await StartAndPresentAsync(viewModel, preview);
        preview.RaiseFailure(PreviewFailureCategory.PreviewStalled);
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
        Assert.False(surface.IsVideoVisible);
        Assert.Equal("Video input stalled", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task StartupTimeoutIsAControlledNonHresultFailure()
    {
        var (viewModel, preview, surface) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        preview.CompleteStart();
        await start;
        preview.CompleteStop();
        preview.RaiseFailure(PreviewFailureCategory.StartupTimeout);
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
        Assert.Contains("timed out", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(surface.IsVideoVisible);
        Assert.False(surface.IsSurfaceActive);
        Assert.DoesNotContain("0x", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PreviewFailureCategory.D3DInitializationFailure, "GPU preview")]
    [InlineData(PreviewFailureCategory.DeviceRemoved, "graphics device")]
    [InlineData(PreviewFailureCategory.UnsupportedGpuBuffer, "GPU preview path")]
    public async Task TypedStartFailuresProduceSafeActionableStatus(PreviewFailureCategory category, string expectedText)
    {
        var (viewModel, preview, _) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        preview.CompleteStartFailure(category);
        preview.CompleteStop();
        await start;
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
        Assert.Contains(expectedText, viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopDuringStartingMakesOldStartupCompletionStale()
    {
        var (viewModel, preview, _) = Create();
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        var stop = viewModel.StopPreviewForTestingAsync();
        preview.CompleteStop();
        await stop;
        preview.CompleteStart();
        await start;
        preview.RaiseFirstFrame();
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);
    }

    [Fact]
    public async Task DoubleStartCreatesOneSession()
    {
        var (viewModel, preview, _) = Create();
        await MakeReadyAsync(viewModel);
        var first = viewModel.StartPreviewForTestingAsync();
        await viewModel.StartPreviewForTestingAsync();
        Assert.Equal(1, preview.StartCalls);
        preview.CompleteStart();
        await first;
    }

    [Fact]
    public async Task RepeatedStopCreatesOneStopOperation()
    {
        var (viewModel, preview, _) = Create();
        await StartAndPresentAsync(viewModel, preview);
        var first = viewModel.StopPreviewForTestingAsync();
        var second = viewModel.StopPreviewForTestingAsync();
        preview.CompleteStop();
        await Task.WhenAll(first, second);
        Assert.Equal(1, preview.StopCalls);
    }

    [Fact]
    public async Task ActivePreviewDisablesSelectorsRefreshAndLaterFeatures()
    {
        var (viewModel, preview, _) = Create();
        await StartAndPresentAsync(viewModel, preview);
        Assert.False(viewModel.HasDevices);
        Assert.False(viewModel.HasFormats);
        Assert.False(viewModel.CanRefreshDevices);
        Assert.Equal("Stop", viewModel.StartStopText);
        Assert.False(viewModel.CanFullscreen);
        Assert.False(viewModel.CanTakeSnapshot);
        Assert.False(viewModel.CanRecord);
    }

    [Fact]
    public async Task DiagnosticsUpdateStatusWithoutExposingLatencyClaim()
    {
        var (viewModel, preview, _) = Create();
        await StartAndPresentAsync(viewModel, preview);
        preview.RaiseDiagnostics(29.8, 1.2, 2.4);
        Assert.Contains("29.8 rendered fps", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("latency", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.2, viewModel.CurrentPreviewDiagnostics!.AverageProcessingMilliseconds);
    }

    [Fact]
    public async Task DisposalDuringStartingStopsAndUnsubscribesOnce()
    {
        var (viewModel, preview, _) = Create(immediateStop: true);
        await MakeReadyAsync(viewModel);
        _ = viewModel.StartPreviewForTestingAsync();
        viewModel.Dispose();
        preview.CompleteStart();
        Assert.Equal(1, preview.StopCalls);
        Assert.Equal(1, preview.FirstFrameRemoveCalls);
        Assert.Equal(1, preview.DiagnosticsRemoveCalls);
        Assert.Equal(1, preview.FailureRemoveCalls);
    }

    [Fact]
    public async Task StaleEventsAfterDisposalDoNotMutateUi()
    {
        var (viewModel, preview, surface) = Create(immediateStop: true);
        await StartAndPresentAsync(viewModel, preview);
        viewModel.Dispose();
        var title = viewModel.PreviewTitle;
        preview.RaiseFailure(PreviewFailureCategory.DeviceRemoved);
        Assert.Equal(title, viewModel.PreviewTitle);
        Assert.False(surface.IsVideoVisible);
    }

    private static async Task StartAndPresentAsync(MainWindowViewModel viewModel, FakePreviewService preview)
    {
        await MakeReadyAsync(viewModel);
        var start = viewModel.StartPreviewForTestingAsync();
        preview.CompleteStart();
        await start;
        preview.RaiseFirstFrame();
    }

    private static async Task MakeReadyAsync(MainWindowViewModel viewModel)
    {
        viewModel.Devices.Add(Camera);
        viewModel.SelectedDevice = Camera;
        await viewModel.FormatDiscoveryCompletion;
        viewModel.SelectedFormat = Format;
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);
    }

    private static (MainWindowViewModel ViewModel, FakePreviewService Preview, FakeSurface Surface) Create(bool immediateStop = false)
    {
        var preview = new FakePreviewService(immediateStop);
        var surface = new FakeSurface();
        var viewModel = new MainWindowViewModel(
            NullApplicationLogger.Instance,
            discoveryService: new ImmediateDiscoveryService(),
            previewService: preview,
            previewSurface: surface,
            synchronizationContext: new ImmediateSynchronizationContext());
        return (viewModel, preview, surface);
    }

    private sealed class ImmediateDiscoveryService : ICaptureDeviceDiscoveryService
    {
        public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]));

        public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));

        public void Dispose() { }
    }

    private sealed class FakeSurface : IPreviewSurface
    {
        public nint Handle => 42;
        public PreviewSurfaceSize PixelSize => new(1280, 720);
        public bool IsAvailable { get; set; } = true;
        public bool IsVideoVisible { get; private set; }
        public bool IsSurfaceActive { get; private set; }
        public event EventHandler<PreviewSurfaceSize>? PixelSizeChanged { add { } remove { } }
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public void SetSurfaceActive(bool active) => IsSurfaceActive = active;
        public void SetVideoVisible(bool visible) => IsVideoVisible = visible;
    }

    private sealed class FakePreviewService : ICapturePreviewService
    {
        private readonly TaskCompletionSource<PreviewStartResult> start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PreviewStopResult> stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<PreviewSessionEventArgs>? firstFrame;
        private EventHandler<PreviewDiagnosticsEventArgs>? diagnostics;
        private EventHandler<PreviewFailureEventArgs>? failure;
        private readonly Guid sessionId = Guid.NewGuid();

        public FakePreviewService(bool immediateStop)
        {
            if (immediateStop) stop.TrySetResult(PreviewStopResult.Stopped);
        }

        public bool IsActive { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int FirstFrameRemoveCalls { get; private set; }
        public int DiagnosticsRemoveCalls { get; private set; }
        public int FailureRemoveCalls { get; private set; }

        public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented
        {
            add => firstFrame += value;
            remove { firstFrame -= value; FirstFrameRemoveCalls++; }
        }

        public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated
        {
            add => diagnostics += value;
            remove { diagnostics -= value; DiagnosticsRemoveCalls++; }
        }

        public event EventHandler<PreviewFailureEventArgs>? PreviewFailed
        {
            add => failure += value;
            remove { failure -= value; FailureRemoveCalls++; }
        }

        public Task<PreviewStartResult> StartAsync(PreviewStartRequest request)
        {
            StartCalls++;
            IsActive = true;
            return start.Task;
        }

        public async Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            var result = await stop.Task;
            IsActive = false;
            return result;
        }

        public void CompleteStart() => start.TrySetResult(PreviewStartResult.Started(sessionId));
        public void CompleteStartCancelled() => start.TrySetResult(PreviewStartResult.Cancelled(sessionId));
        public void CompleteStartFailure(PreviewFailureCategory category) => start.TrySetResult(PreviewStartResult.Failed(sessionId, new PreviewFailure(category, "Safe failure")));
        public void CompleteStop() => stop.TrySetResult(PreviewStopResult.Stopped);
        public void RaiseFirstFrame() => firstFrame?.Invoke(this, new PreviewSessionEventArgs(sessionId));
        public void RaiseFailure(PreviewFailureCategory category) => failure?.Invoke(this, new PreviewFailureEventArgs(sessionId, new PreviewFailure(category, "Safe runtime failure")));
        public void RaiseDiagnostics(double fps, double average, double p95) => diagnostics?.Invoke(this, new PreviewDiagnosticsEventArgs(sessionId, PreviewDiagnostics.Empty(Camera.DisplayName, Format.DisplayLabel) with { RenderedFramesPerSecond = fps, AverageProcessingMilliseconds = average, P95ProcessingMilliseconds = p95 }));
        public void Dispose() { }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}

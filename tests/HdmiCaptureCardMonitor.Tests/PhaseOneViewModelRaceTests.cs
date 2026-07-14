using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseOneViewModelRaceTests
{
    private static readonly CaptureDevice Camera = new("opaque-camera-link", "HD Camera", "HD Camera", true, CaptureBackend.MediaFoundation);
    private static readonly CaptureDevice SecondCamera = new("opaque-second-link", "Second Camera", "Second Camera", true, CaptureBackend.MediaFoundation);
    private static readonly NativeVideoCapability Format = new(0, 0, 1280, 720, 30, 1, 30, Guid.NewGuid(), "MJPEG", VideoInterlaceMode.Progressive, 1, 1, "1280 × 720p · 30 fps · MJPEG");

    [Fact]
    public async Task InitialZeroDeviceResultShowsHonestEmptyState()
    {
        var (viewModel, fake, _) = Create();
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([]));
        await refresh;

        Assert.Empty(viewModel.Devices);
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.Equal("No capture device detected", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task InitialAndRepeatedRefreshSuccessReplaceDeviceList()
    {
        var (viewModel, fake, _) = Create();
        var first = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]));
        await first;
        Assert.Equal([Camera], viewModel.Devices);

        var second = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[1].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([SecondCamera]));
        await second;
        Assert.Equal([SecondCamera], viewModel.Devices);
    }

    [Fact]
    public async Task CurrentRefreshFailureClearsPreviouslyUsableDevices()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);

        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[1].Complete(FailedDevices(DiscoveryFailureCategory.DeviceUnavailable));
        await refresh;

        Assert.Empty(viewModel.Devices);
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
    }

    [Fact]
    public async Task InitialFailureAndRetryFromFaultedAreSupported()
    {
        var (viewModel, fake, _) = Create();
        var first = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(FailedDevices(DiscoveryFailureCategory.Unknown));
        await first;
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);

        var retry = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[1].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]));
        await retry;
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.Equal([Camera], viewModel.Devices);
    }

    [Fact]
    public async Task MissingComponentsFailureUsesSpecificCustomerWording()
    {
        var (viewModel, fake, _) = Create();
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(FailedDevices(DiscoveryFailureCategory.MissingMediaComponents));
        await refresh;

        Assert.Contains("Media Feature Pack", viewModel.PreviewDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", viewModel.PreviewDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceSelectionSupportsFormatsFailureNoFormatsAndClearing()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);

        viewModel.SelectedDevice = Camera;
        var success = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        await success;
        Assert.Equal([Format], viewModel.Formats);
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);

        viewModel.SelectedDevice = SecondCamera;
        var noFormats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[1].Complete(FailedFormats(DiscoveryFailureCategory.NoUsableFormats));
        await noFormats;
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);

        viewModel.SelectedDevice = null;
        await viewModel.FormatDiscoveryCompletion;
        Assert.Empty(viewModel.Formats);
        Assert.Null(viewModel.SelectedDevice);
    }

    [Theory]
    [InlineData(DiscoveryFailureCategory.NoUsableFormats)]
    [InlineData(DiscoveryFailureCategory.DeviceUnavailable)]
    public async Task CurrentCapabilityFailureEntersFaulted(DiscoveryFailureCategory category)
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var formats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(FailedFormats(category));
        await formats;

        Assert.Empty(viewModel.Formats);
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
    }

    [Fact]
    public async Task ClearingReadySelectionReturnsToIdle()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var formats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        await formats;
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);

        viewModel.SelectedDevice = null;
        await viewModel.FormatDiscoveryCompletion;
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.Empty(viewModel.Formats);
    }

    [Fact]
    public async Task RefreshWhileDeviceReadyClearsSelectionAndFormats()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var formats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        await formats;

        var refresh = viewModel.RefreshDevicesForTestingAsync();
        Assert.Null(viewModel.SelectedDevice);
        Assert.Empty(viewModel.Formats);
        fake.DeviceRequests[1].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([SecondCamera]));
        await refresh;
        Assert.Equal([SecondCamera], viewModel.Devices);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task OlderRefreshCannotAlterNewerRefresh(string oldCompletion)
    {
        var (viewModel, fake, _) = Create();
        var oldRefresh = viewModel.RefreshDevicesForTestingAsync();
        var newRefresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[1].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([SecondCamera]));
        await newRefresh;

        fake.DeviceRequests[0].Complete(oldCompletion switch
        {
            "success" => DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]),
            "failure" => FailedDevices(DiscoveryFailureCategory.Unknown),
            _ => DiscoveryResults.Cancelled<IReadOnlyList<CaptureDevice>>()
        });
        await oldRefresh;

        Assert.Equal([SecondCamera], viewModel.Devices);
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderFormatCompletionCannotAlterNewerSelection(bool oldFails)
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera, SecondCamera);
        viewModel.SelectedDevice = Camera;
        var oldFormats = viewModel.FormatDiscoveryCompletion;
        viewModel.SelectedDevice = SecondCamera;
        var newFormats = viewModel.FormatDiscoveryCompletion;
        var secondFormat = Format with { NativeMediaTypeIndex = 2, DisplayLabel = "960 × 540p · 30 fps · NV12" };
        fake.FormatRequests[1].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([secondFormat]));
        await newFormats;

        fake.FormatRequests[0].Complete(oldFails ? FailedFormats(DiscoveryFailureCategory.DeviceUnavailable) : DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        await oldFormats;

        Assert.Equal(SecondCamera, viewModel.SelectedDevice);
        Assert.Equal([secondFormat], viewModel.Formats);
        Assert.Equal(CaptureSessionState.DeviceReady, viewModel.SessionState);
    }

    [Fact]
    public async Task CurrentCancellationDoesNotEnterFaulted()
    {
        var (viewModel, fake, _) = Create();
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(DiscoveryResults.Cancelled<IReadOnlyList<CaptureDevice>>());
        await refresh;
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.False(viewModel.IsDeviceScanRunning);
        Assert.Equal("No capture device detected", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task SuccessfulEmptyCapabilityResultIsTreatedAsNoUsableFormats()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var formats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([]));
        await formats;

        Assert.Equal(Camera, viewModel.SelectedDevice);
        Assert.Empty(viewModel.Formats);
        Assert.False(viewModel.HasFormats);
        Assert.Equal(CaptureSessionState.Faulted, viewModel.SessionState);
        Assert.Equal("No native formats available", viewModel.PreviewTitle);
        Assert.Equal("Select another capture device or refresh the device list.", viewModel.PreviewDescription);
        Assert.Equal("The selected device exposed no usable native video formats.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ClearingSelectionWhileFormatDiscoveryIsBlockedCancelsAndResetsText()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var blocked = viewModel.FormatDiscoveryCompletion;
        Assert.Equal("Reading supported formats…", viewModel.FormatPlaceholder);

        viewModel.SelectedDevice = null;
        await viewModel.FormatDiscoveryCompletion;

        Assert.True(fake.FormatRequests[0].IsCancellationRequested);
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.Empty(viewModel.Formats);
        Assert.Equal("Select a video capture device.", viewModel.StatusMessage);
        Assert.Equal("Select a capture device", viewModel.PreviewTitle);
        Assert.Equal("Choose a Windows video input below to inspect its supported formats.", viewModel.PreviewDescription);
        Assert.Equal("Select a device first", viewModel.FormatPlaceholder);

        fake.FormatRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        await blocked;
        Assert.Empty(viewModel.Formats);
        Assert.Equal("Select a capture device", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task CurrentFormatCancellationReturnsToIdleWithoutFaulting()
    {
        var (viewModel, fake, _) = Create();
        await PopulateDeviceAsync(viewModel, fake, Camera);
        viewModel.SelectedDevice = Camera;
        var formats = viewModel.FormatDiscoveryCompletion;
        fake.FormatRequests[0].Complete(DiscoveryResults.Cancelled<IReadOnlyList<NativeVideoCapability>>());
        await formats;

        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
        Assert.False(viewModel.IsFormatScanRunning);
        Assert.Empty(viewModel.Formats);
        Assert.Equal("Format discovery cancelled", viewModel.PreviewTitle);
    }

    [Fact]
    public async Task AccessDeniedUsesCameraPrivacyGuidanceWithoutDiagnostics()
    {
        var (viewModel, fake, _) = Create();
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(FailedDevices(DiscoveryFailureCategory.AccessDenied));
        await refresh;

        var text = string.Join(' ', viewModel.StatusMessage, viewModel.PreviewTitle, viewModel.PreviewDescription);
        Assert.Contains("camera access", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Camera.Id, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposalPreventsLaterMutationAndUnsubscribesStateMachine()
    {
        var fake = new ControllableDiscoveryService();
        var stateMachine = new CaptureSessionStateMachine();
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, stateMachine: stateMachine, discoveryService: fake);
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        viewModel.Dispose();
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        fake.DeviceRequests[0].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]));
        await refresh;
        Assert.True(stateMachine.TryTransitionTo(CaptureSessionState.Faulted));

        Assert.Empty(viewModel.Devices);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task OpaqueDeviceIdentifierNeverReachesCustomerTextOrLogs()
    {
        var (viewModel, fake, logger) = Create();
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[0].Complete(DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, DiscoveryFailureCategory.Unknown, unchecked((int)0x80004005), $"Failed for {Camera.Id}")));
        await refresh;

        var text = string.Join(' ', viewModel.StatusMessage, viewModel.PreviewTitle, viewModel.PreviewDescription, string.Join(' ', logger.Messages));
        Assert.DoesNotContain(Camera.Id, text, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseOneCaptureActionsRemainUnavailable()
    {
        var (viewModel, _, _) = Create();
        Assert.False(viewModel.CanStartCapture);
        Assert.False(viewModel.CanFullscreen);
        Assert.False(viewModel.CanTakeSnapshot);
        Assert.False(viewModel.CanRecord);
    }

    private static async Task PopulateDeviceAsync(MainWindowViewModel viewModel, ControllableDiscoveryService fake, params CaptureDevice[] devices)
    {
        var refresh = viewModel.RefreshDevicesForTestingAsync();
        fake.DeviceRequests[^1].Complete(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>(devices));
        await refresh;
    }

    private static (MainWindowViewModel ViewModel, ControllableDiscoveryService Fake, RecordingLogger Logger) Create()
    {
        var fake = new ControllableDiscoveryService();
        var logger = new RecordingLogger();
        return (new MainWindowViewModel(logger, discoveryService: fake), fake, logger);
    }

    private static DiscoveryResult<IReadOnlyList<CaptureDevice>> FailedDevices(DiscoveryFailureCategory category) =>
        DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, category, unchecked((int)0x80004005), "Safe failure"));

    private static DiscoveryResult<IReadOnlyList<NativeVideoCapability>> FailedFormats(DiscoveryFailureCategory category) =>
        DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, category, unchecked((int)0x80004005), "Safe failure"));

    private sealed class ControllableDiscoveryService : ICaptureDeviceDiscoveryService
    {
        public List<Pending<IReadOnlyList<CaptureDevice>>> DeviceRequests { get; } = [];
        public List<Pending<IReadOnlyList<NativeVideoCapability>>> FormatRequests { get; } = [];

        public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken)
        {
            var pending = new Pending<IReadOnlyList<CaptureDevice>>(cancellationToken);
            DeviceRequests.Add(pending);
            return pending.Task;
        }

        public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken)
        {
            var pending = new Pending<IReadOnlyList<NativeVideoCapability>>(cancellationToken);
            FormatRequests.Add(pending);
            return pending.Task;
        }

        public void Dispose() { }
    }

    private sealed class Pending<T>(CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<DiscoveryResult<T>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DiscoveryResult<T>> Task => completion.Task;
        public bool IsCancellationRequested => cancellationToken.IsCancellationRequested;
        public void Complete(DiscoveryResult<T> result) => completion.TrySetResult(result);
    }

    private sealed class RecordingLogger : IApplicationLogger
    {
        public List<string> Messages { get; } = [];
        public void Debug(string message) => Messages.Add(message);
        public void Information(string message) => Messages.Add(message);
        public void Warning(string message) => Messages.Add(message);
        public void LogError(string message, Exception? exception = null) => Messages.Add(message);
    }
}

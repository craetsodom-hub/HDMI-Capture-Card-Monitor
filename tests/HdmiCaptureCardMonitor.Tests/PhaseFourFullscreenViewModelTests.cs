using System.Xml.Linq;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.Presentation.Fullscreen;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseFourFullscreenViewModelTests
{
    private static readonly CaptureDevice Camera = new("opaque-camera-link", "HD Camera", "HD Camera", true, CaptureBackend.MediaFoundation);
    private static readonly NativeVideoCapability Format = new(0, 0, 1280, 720, 30, 1, 30, Guid.NewGuid(), "MJPEG", VideoInterlaceMode.Progressive, 1, 1, "1280 x 720p - 30 fps - MJPEG");

    [Theory]
    [InlineData(CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.DeviceReady)]
    [InlineData(CaptureSessionState.Starting)]
    [InlineData(CaptureSessionState.Stopping)]
    [InlineData(CaptureSessionState.Faulted)]
    public void FullscreenIsUnavailableOutsidePresentedPreview(CaptureSessionState state)
    {
        var stateMachine = MoveTo(state);
        var controller = new FakeFullscreenController();
        using var viewModel = new MainWindowViewModel(
            NullApplicationLogger.Instance,
            stateMachine: stateMachine,
            previewSurface: new FakeSurface(),
            fullscreenController: controller,
            synchronizationContext: new ImmediateSynchronizationContext());

        Assert.False(viewModel.CanToggleFullscreen);
        Assert.False(viewModel.ToggleFullscreenCommand.CanExecute(null));
    }

    [Fact]
    public async Task PresentedPreviewEnablesFullscreenAndDialogOrTransitionDisablesIt()
    {
        var context = Create();
        await MakePreviewingAsync(context);

        Assert.True(context.ViewModel.CanToggleFullscreen);
        context.ViewModel.ShowHelpInformationCommand.Execute(null);
        Assert.False(context.ViewModel.CanToggleFullscreen);
        context.ViewModel.CloseInformationDialogCommand.Execute(null);
        Assert.True(context.ViewModel.CanToggleFullscreen);

        context.Controller.SetTransitioning(true);
        Assert.False(context.ViewModel.CanToggleFullscreen);
        context.Controller.SetTransitioning(false);
        context.Surface.SetPresentable(false);
        Assert.False(context.ViewModel.CanToggleFullscreen);
    }

    [Fact]
    public async Task FullscreenTogglePreservesCaptureIdentitySelectionAndDiagnostics()
    {
        var context = Create();
        await MakePreviewingAsync(context);
        context.Preview.RaiseDiagnostics(10.2);
        var session = context.ViewModel.ActivePreviewSessionId;
        var handle = context.ViewModel.PreviewSurfaceHandle;
        var device = context.ViewModel.SelectedDevice;
        var format = context.ViewModel.SelectedFormat;
        var diagnostics = context.ViewModel.CurrentPreviewDiagnostics;

        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);
        Assert.True(context.ViewModel.IsFullscreen);
        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.IsFullscreen);
        Assert.Equal(session, context.ViewModel.ActivePreviewSessionId);
        Assert.Equal(handle, context.ViewModel.PreviewSurfaceHandle);
        Assert.Same(device, context.ViewModel.SelectedDevice);
        Assert.Same(format, context.ViewModel.SelectedFormat);
        Assert.Same(diagnostics, context.ViewModel.CurrentPreviewDiagnostics);
        Assert.Equal(1, context.Preview.StartCalls);
        Assert.Equal(0, context.Preview.StopCalls);
    }

    [Fact]
    public async Task StopWhileFullscreenRestoresPresentationBeforeStoppingCapture()
    {
        var order = new List<string>();
        var context = Create(order);
        await MakePreviewingAsync(context);
        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);
        order.Clear();

        await context.ViewModel.StopPreviewForTestingAsync();

        Assert.Equal(["exit:Stop", "stop"], order);
        Assert.False(context.ViewModel.IsFullscreen);
        Assert.Equal(CaptureSessionState.DeviceReady, context.ViewModel.SessionState);
    }

    [Fact]
    public async Task RuntimeFailureRestoresPresentationBeforeCaptureCleanup()
    {
        var order = new List<string>();
        var context = Create(order);
        await MakePreviewingAsync(context);
        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);
        order.Clear();

        context.Preview.RaiseFailure(PreviewFailureCategory.DeviceRemoved);
        await WaitUntilAsync(() => context.Preview.StopCalls == 1);

        Assert.Equal("exit:PreviewFailure", order[0]);
        Assert.Equal("stop", order[1]);
        Assert.False(context.ViewModel.IsFullscreen);
    }

    [Fact]
    public void HelpExplainsFullscreenKeysAndCaptureContinuity()
    {
        using var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);
        viewModel.ShowHelpInformationCommand.Execute(null);
        var help = string.Join(' ', viewModel.InformationDialogDescription, viewModel.InformationDialogDetails);

        Assert.Contains("active live preview", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("F11", help, StringComparison.Ordinal);
        Assert.Contains("Escape", help, StringComparison.Ordinal);
        Assert.Contains("Capture remains active", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullscreenLayoutRetainsOnePreviewHostAndCollapsesOnlyWindowChrome()
    {
        var root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var previewHosts = window.Descendants().Where(element => element.Name.LocalName == "HwndPreviewSurface").ToArray();
        var upcoming = window.Descendants().Single(element => string.Equals((string?)element.Attribute(x + "Name"), "UpcomingActions", StringComparison.Ordinal));
        var fullscreen = window.Descendants().Single(element => string.Equals((string?)element.Attribute(x + "Name"), "FullscreenButton", StringComparison.Ordinal));

        Assert.Single(previewHosts);
        Assert.Equal(2, upcoming.Descendants().Count(element => element.Name.LocalName == "Button"));
        Assert.DoesNotContain(fullscreen, upcoming.Descendants());
        Assert.Equal("{Binding FullscreenAccessText}", (string?)fullscreen.Attribute("Content"));
        Assert.Contains("IsWindowedChromeVisible", window.ToString(), StringComparison.Ordinal);
        Assert.Contains("IsFullscreenPresentation", window.ToString(), StringComparison.Ordinal);
        Assert.Contains("BorderThickness\" Value=\"0", window.ToString(), StringComparison.Ordinal);
        Assert.Contains("CornerRadius\" Value=\"0", window.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FullscreenPresentationSourcesCannotStartOrStopCapture()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Presentation", "Fullscreen");
        var source = string.Join('\n', Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));
        var surface = File.ReadAllText(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Controls", "HwndPreviewSurface.cs"));

        Assert.DoesNotContain("previewService", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartAsync(request", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowCursor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetParent", source, StringComparison.Ordinal);
        Assert.Contains("WmMouseMove", surface, StringComparison.Ordinal);
        Assert.Contains("WmSetCursor", surface, StringComparison.Ordinal);
        Assert.Contains("SetCursor(default)", surface, StringComparison.Ordinal);
    }

    private static TestContext Create(List<string>? order = null)
    {
        order ??= [];
        var preview = new FakePreviewService(order);
        var surface = new FakeSurface();
        var controller = new FakeFullscreenController(order);
        var viewModel = new MainWindowViewModel(
            NullApplicationLogger.Instance,
            discoveryService: new ImmediateDiscoveryService(),
            previewService: preview,
            previewSurface: surface,
            fullscreenController: controller,
            synchronizationContext: new ImmediateSynchronizationContext());
        return new TestContext(viewModel, preview, surface, controller);
    }

    private static async Task MakePreviewingAsync(TestContext context)
    {
        context.ViewModel.Devices.Add(Camera);
        context.ViewModel.SelectedDevice = Camera;
        await context.ViewModel.FormatDiscoveryCompletion;
        context.ViewModel.SelectedFormat = Format;
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
    }

    private static CaptureSessionStateMachine MoveTo(CaptureSessionState target)
    {
        var machine = new CaptureSessionStateMachine();
        if (target == CaptureSessionState.Idle) return machine;
        if (target == CaptureSessionState.Enumerating) { machine.TryTransitionTo(target); return machine; }
        if (target == CaptureSessionState.DeviceReady) { machine.TryTransitionTo(CaptureSessionState.Enumerating); machine.TryTransitionTo(CaptureSessionState.DeviceReady); return machine; }
        machine.TryTransitionTo(CaptureSessionState.Enumerating);
        machine.TryTransitionTo(CaptureSessionState.DeviceReady);
        if (target == CaptureSessionState.Starting) { machine.TryTransitionTo(target); return machine; }
        if (target == CaptureSessionState.Stopping) { machine.TryTransitionTo(CaptureSessionState.Starting); machine.TryTransitionTo(CaptureSessionState.Previewing); machine.TryTransitionTo(target); return machine; }
        if (target == CaptureSessionState.Faulted) { machine.TryTransitionTo(target); return machine; }
        return machine;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100 && !predicate(); attempt++) await Task.Yield();
        Assert.True(predicate());
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HDMI-Capture-Card-Monitor.sln"))) directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record TestContext(MainWindowViewModel ViewModel, FakePreviewService Preview, FakeSurface Surface, FakeFullscreenController Controller);

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
        public PreviewSurfaceSize PixelSize { get; private set; } = new(1280, 720);
        public bool IsAvailable { get; private set; } = true;
        public bool IsPresentable { get; private set; } = true;
        public bool IsVideoVisible { get; private set; }
        public event EventHandler<PreviewSurfaceSize>? PixelSizeChanged { add { } remove { } }
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public event EventHandler? PresentabilityChanged;
        public void SetSurfaceActive(bool active) { _ = active; }
        public void SetVideoVisible(bool visible) => IsVideoVisible = visible;
        public void SetPresentable(bool value)
        {
            IsPresentable = value;
            IsAvailable = value;
            PixelSize = value ? new PreviewSurfaceSize(1280, 720) : default;
            PresentabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakePreviewService(List<string> order) : ICapturePreviewService
    {
        private readonly Guid sessionId = Guid.NewGuid();
        public bool IsActive { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public event EventHandler? IsActiveChanged;
        public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented;
        public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated;
        public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;
        public Task<PreviewStartResult> StartAsync(PreviewStartRequest request)
        {
            _ = request;
            StartCalls++;
            IsActive = true;
            IsActiveChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(PreviewStartResult.Started(sessionId));
        }
        public Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            order.Add("stop");
            StopCalls++;
            IsActive = false;
            IsActiveChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(PreviewStopResult.Stopped);
        }
        public void RaiseFirstFrame() => FirstFramePresented?.Invoke(this, new PreviewSessionEventArgs(sessionId));
        public void RaiseDiagnostics(double fps) => DiagnosticsUpdated?.Invoke(this, new PreviewDiagnosticsEventArgs(sessionId, PreviewDiagnostics.Empty(Camera.DisplayName, Format.DisplayLabel) with { FramesReceivedPerSecond = fps, RenderedFramesPerSecond = fps }));
        public void RaiseFailure(PreviewFailureCategory category) => PreviewFailed?.Invoke(this, new PreviewFailureEventArgs(sessionId, new PreviewFailure(category, "Safe failure")));
        public void Dispose() { }
    }

    private sealed class FakeFullscreenController(List<string>? order = null) : IFullscreenWindowController
    {
        private readonly List<string> order = order ?? [];
        public bool IsFullscreen { get; private set; }
        public bool IsTransitioning { get; private set; }
        public long Generation { get; private set; }
        public event EventHandler<FullscreenControllerStateChangedEventArgs>? StateChanged;
        public Task<FullscreenTransitionResult> EnterAsync()
        {
            Generation++;
            IsFullscreen = true;
            StateChanged?.Invoke(this, new FullscreenControllerStateChangedEventArgs(true, false, Generation));
            return Task.FromResult(FullscreenTransitionResult.Applied);
        }
        public Task<FullscreenTransitionResult> ExitAsync(FullscreenExitReason reason)
        {
            order.Add($"exit:{reason}");
            Generation++;
            IsFullscreen = false;
            IsTransitioning = false;
            StateChanged?.Invoke(this, new FullscreenControllerStateChangedEventArgs(false, false, Generation));
            return Task.FromResult(FullscreenTransitionResult.Applied);
        }
        public void SetTransitioning(bool value)
        {
            IsTransitioning = value;
            StateChanged?.Invoke(this, new FullscreenControllerStateChangedEventArgs(IsFullscreen, value, ++Generation));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}

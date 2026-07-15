using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Capture.Preview;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.Presentation;
using HdmiCaptureCardMonitor.Presentation.Fullscreen;
using HdmiCaptureCardMonitor.ViewModels;
using System.Xml.Linq;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseFiveAudioLifecycleTests
{
    private static readonly CaptureDevice Camera = new("opaque-video", "HD Camera", "HD Camera", true, CaptureBackend.MediaFoundation);
    private static readonly NativeVideoCapability Format = new(0, 0, 1280, 720, 30, 1, 30, Guid.NewGuid(), "NV12", VideoInterlaceMode.Progressive, 1, 1, "1280 × 720p · 30 fps · NV12");
    private static readonly AudioEndpoint Microphone = new("opaque-audio-input", "Laptop microphone", AudioEndpointDataFlow.Capture);
    private static readonly AudioEndpoint Headphones = new("opaque-audio-output", "Wired headphones", AudioEndpointDataFlow.Render);

    [Fact]
    public async Task RefreshEnumeratesAudioSeparatelyAndRepresentsDefaultsWithoutFakeEndpoints()
    {
        using var context = Create();
        await context.ViewModel.RefreshDevicesForTestingAsync();
        Assert.Collection(context.ViewModel.AudioInputs,
            choice => { Assert.Equal("No audio", choice.DisplayName); Assert.Null(choice.Endpoint); },
            choice => Assert.Same(Microphone, choice.Endpoint));
        Assert.Collection(context.ViewModel.AudioOutputs,
            choice => { Assert.Equal("System default output", choice.DisplayName); Assert.Null(choice.Endpoint); Assert.True(choice.IsSystemDefaultOutput); },
            choice => Assert.Same(Headphones, choice.Endpoint));
    }

    [Fact]
    public async Task VideoStartsWithoutAudioAndCreatesNoAudioSession()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: false);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await Task.Yield();
        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
        Assert.Equal(0, context.Audio.StartCalls);
        Assert.Equal("Audio off", context.ViewModel.AudioStatusText);
    }

    [Fact]
    public async Task SelectedAudioStartsOnlyAfterRealFirstVideoFrame()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        Assert.Equal(0, context.Audio.StartCalls);
        Assert.Equal("Waiting for live video", context.ViewModel.AudioStatusText);
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.StartCalls == 1);
        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
        Assert.Equal(AudioMonitorState.Monitoring, context.ViewModel.AudioMonitorState);
    }

    [Fact]
    public async Task AudioStartupFailureLeavesVideoPreviewing()
    {
        using var context = Create(audioStartFailure: new AudioMonitorFailure(AudioMonitorFailureCategory.AccessDenied, "Allow microphone access."));
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.ViewModel.AudioMonitorState == AudioMonitorState.Faulted);
        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
        Assert.Equal("Video is live. Audio monitoring could not start.", context.ViewModel.StatusMessage);
    }

    [Fact]
    public async Task RuntimeAudioFailureUsesStoppedWordingAndLeavesVideoPreviewing()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);

        context.Audio.RaiseFailure(new AudioMonitorFailure(
            AudioMonitorFailureCategory.AudioProcessingFailure,
            "Audio processing failed."));

        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
        Assert.Equal("Video is live. Audio monitoring stopped.", context.ViewModel.StatusMessage);
    }

    [Fact]
    public async Task DeviceInvalidationUsesDisconnectedWordingAndLeavesVideoPreviewing()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);

        context.Audio.RaiseFailure(new AudioMonitorFailure(
            AudioMonitorFailureCategory.DeviceInvalidated,
            "The endpoint disappeared."));

        Assert.Equal(CaptureSessionState.Previewing, context.ViewModel.SessionState);
        Assert.Equal("Video is live. The selected audio device disconnected.", context.ViewModel.StatusMessage);
        Assert.DoesNotContain("opaque", context.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopOrdersAudioBeforeVideo()
    {
        var order = new List<string>();
        using var context = Create(order);
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        await context.ViewModel.StopPreviewForTestingAsync();
        Assert.Equal(["audio-stop", "video-stop"], order);
        Assert.Equal(CaptureSessionState.DeviceReady, context.ViewModel.SessionState);
    }

    [Fact]
    public async Task RepeatedStopDoesNotRepeatAudioOrVideoCleanup()
    {
        var order = new List<string>();
        using var context = Create(order);
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);

        await context.ViewModel.StopPreviewForTestingAsync();
        await context.ViewModel.StopPreviewForTestingAsync();

        Assert.Equal(["audio-stop", "video-stop"], order);
    }

    [Fact]
    public async Task StopTimeoutRetainsAudioOwnershipAndPreventsUnsafeRestart()
    {
        using var context = Create(audioStopTimeout: true);
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);

        await context.ViewModel.StopPreviewForTestingAsync();

        Assert.True(context.Audio.IsActive);
        Assert.False(context.ViewModel.CanStartCapture);
        Assert.False(context.ViewModel.CanChangeAudioSelection);
        Assert.Equal("Audio cleanup timed out", context.ViewModel.AudioStatusText);
    }

    [Fact]
    public async Task FullscreenPresentationDoesNotRestartAudioSession()
    {
        var fullscreen = new FakeFullscreenController();
        using var context = Create(fullscreenController: fullscreen);
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        var session = context.Audio.ActiveSessionId;

        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);
        await context.ViewModel.ToggleFullscreenCommand.ExecuteAsync(null);

        Assert.Equal(1, context.Audio.StartCalls);
        Assert.Equal(session, context.Audio.ActiveSessionId);
        Assert.True(context.Audio.IsActive);
    }

    [Fact]
    public async Task RuntimeVideoFailureStopsAudioAndPreservesVideoFailureState()
    {
        var order = new List<string>();
        using var context = Create(order);
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        context.Preview.RaiseFailure();
        await WaitUntilAsync(() => order.Count >= 2);
        Assert.Equal("audio-stop", order[0]);
        Assert.Equal("video-stop", order[1]);
        Assert.False(context.Audio.IsActive);
    }

    [Fact]
    public async Task EndpointSelectorsLockDuringPreviewAndVolumeMuteEnableOnlyForAudioSession()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        Assert.True(context.ViewModel.CanChangeAudioSelection);
        Assert.False(context.ViewModel.CanAdjustAudio);
        await context.ViewModel.StartPreviewForTestingAsync();
        Assert.False(context.ViewModel.CanChangeAudioSelection);
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        Assert.True(context.ViewModel.CanAdjustAudio);
    }

    [Fact]
    public async Task VolumeAndMuteAreApplicationLocalControls()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        context.ViewModel.AudioVolume = 50;
        context.ViewModel.ToggleAudioMuteCommand.Execute(null);
        Assert.Equal(50, context.Audio.Volume);
        Assert.True(context.Audio.Muted);
        Assert.Equal("_Unmute", context.ViewModel.MuteAccessText);
    }

    [Fact]
    public async Task StaleAudioEventCannotAffectCurrentSession()
    {
        using var context = Create();
        await MakeReadyAsync(context, selectAudio: true);
        await context.ViewModel.StartPreviewForTestingAsync();
        context.Preview.RaiseFirstFrame();
        await WaitUntilAsync(() => context.Audio.IsActive);
        context.Audio.RaiseState(Guid.NewGuid(), AudioMonitorState.Faulted);
        Assert.Equal(AudioMonitorState.Monitoring, context.ViewModel.AudioMonitorState);
    }

    [Fact]
    public void CustomerFacingAudioTypesAndLogsCannotAccidentallyExposeOpaqueIds()
    {
        Assert.DoesNotContain(Microphone.Id, Microphone.ToString(), StringComparison.Ordinal);
        var root = FindRepositoryRoot();
        var audioSource = string.Join('\n', Directory.GetFiles(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Capture", "Audio"), "*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("IAudioEndpointVolume", audioSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Endpoint.Id}", audioSource, StringComparison.Ordinal);
        Assert.DoesNotContain("end-to-end", audioSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AudioControlsAreAccessibleAndDeferredMediaToolsRemainDisabled()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml"));

        Assert.Contains("AutomationProperties.Name=\"Audio input\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Audio output\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Audio monitoring volume\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Snapshot\" IsEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Record\" IsEnabled=\"False\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioSliderUsesThemedFortyDipTemplateAndDisabledPercentageStyle()
    {
        var root = FindRepositoryRoot();
        var controls = XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Resources", "Themes", "Controls.xaml"));
        var tokens = XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Resources", "Themes", "DesignTokens.xaml"));
        var window = File.ReadAllText(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var slider = controls.Descendants().Single(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == "AudioVolumeSlider");
        var percentage = controls.Descendants().Single(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == "AudioPercentageText");

        Assert.Contains(slider.Descendants(), element => element.Name.LocalName == "Track" && (string?)element.Attribute(x + "Name") == "PART_Track");
        Assert.Contains(slider.Descendants(), element => element.Name.LocalName == "Trigger" && (string?)element.Attribute("Property") == "IsKeyboardFocusWithin");
        Assert.Contains(slider.Descendants(), element => element.Name.LocalName == "Trigger" && (string?)element.Attribute("Property") == "IsEnabled" && (string?)element.Attribute("Value") == "False");
        Assert.Contains(slider.Descendants(), element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Height" && (string?)element.Attribute("Value") == "{StaticResource ControlHeight}");
        Assert.Contains(percentage.Descendants(), element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Value") == "{DynamicResource DisabledTextBrush}");
        Assert.Contains(tokens.Descendants(), element => (string?)element.Attribute(x + "Key") == "AudioSliderTrackHeight" && element.Value == "4");
        Assert.Contains(tokens.Descendants(), element => (string?)element.Attribute(x + "Key") == "AudioSliderThumbSize" && element.Value == "16");
        Assert.Contains("Style=\"{StaticResource AudioVolumeSlider}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanAdjustAudio}\" Style=\"{StaticResource AudioPercentageText}\"", window, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Audio monitoring volume\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewKeyDown=", slider.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(720, 1.0, true)]
    [InlineData(720, 1.5, true)]
    [InlineData(720, 2.0, true)]
    [InlineData(900, 1.0, false)]
    [InlineData(900, 1.5, false)]
    [InlineData(900, 2.0, false)]
    [InlineData(1180, 1.0, false)]
    [InlineData(1180, 1.5, false)]
    [InlineData(1180, 2.0, false)]
    public void AudioResponsivePolicyUsesDeviceIndependentWidths(
        double widthInDips,
        double scale,
        bool expectedStacked)
    {
        Assert.True(widthInDips * scale >= widthInDips);
        Assert.Equal(expectedStacked, ResponsiveLayoutPolicy.UsesStackedSelectors(widthInDips));
    }

    private static TestContext Create(
        List<string>? order = null,
        AudioMonitorFailure? audioStartFailure = null,
        bool audioStopTimeout = false,
        IFullscreenWindowController? fullscreenController = null)
    {
        order ??= [];
        var preview = new FakePreviewService(order);
        var audio = new FakeAudioMonitorService(order, audioStartFailure, audioStopTimeout);
        var viewModel = new MainWindowViewModel(
            NullApplicationLogger.Instance,
            discoveryService: new FakeVideoDiscovery(),
            previewService: preview,
            previewSurface: new FakeSurface(),
            fullscreenController: fullscreenController,
            synchronizationContext: new ImmediateSynchronizationContext(),
            audioDiscoveryService: new FakeAudioDiscovery(),
            audioMonitorService: audio);
        return new TestContext(viewModel, preview, audio);
    }

    private static async Task MakeReadyAsync(TestContext context, bool selectAudio)
    {
        context.ViewModel.Devices.Add(Camera);
        context.ViewModel.SelectedDevice = Camera;
        await context.ViewModel.FormatDiscoveryCompletion;
        context.ViewModel.SelectedFormat = Format;
        context.ViewModel.AudioInputs.Clear();
        context.ViewModel.AudioInputs.Add(AudioEndpointChoice.NoAudio);
        var microphoneChoice = new AudioEndpointChoice(Microphone.DisplayName, Microphone);
        context.ViewModel.AudioInputs.Add(microphoneChoice);
        context.ViewModel.SelectedAudioInput = selectAudio ? microphoneChoice : AudioEndpointChoice.NoAudio;
        context.ViewModel.SelectedAudioOutput = AudioEndpointChoice.SystemDefaultOutput;
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
        return directory ?? throw new DirectoryNotFoundException();
    }

    private sealed record TestContext(MainWindowViewModel ViewModel, FakePreviewService Preview, FakeAudioMonitorService Audio) : IDisposable
    {
        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class FakeVideoDiscovery : ICaptureDeviceDiscoveryService
    {
        public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>([Camera]));
        public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) => Task.FromResult(DiscoveryResults.Success<IReadOnlyList<NativeVideoCapability>>([Format]));
        public void Dispose() { }
    }

    private sealed class FakeAudioDiscovery : IAudioEndpointDiscoveryService
    {
        public Task<AudioEndpointDiscoveryResult> EnumerateActiveEndpointsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AudioEndpointDiscoveryResult.Succeeded([Microphone], [Headphones], Headphones));
        public void Dispose() { }
    }

    private sealed class FakePreviewService(List<string> order) : ICapturePreviewService
    {
        private readonly Guid sessionId = Guid.NewGuid();
        public bool IsActive { get; private set; }
        public event EventHandler? IsActiveChanged;
        public event EventHandler<PreviewSessionEventArgs>? FirstFramePresented;
        public event EventHandler<PreviewDiagnosticsEventArgs>? DiagnosticsUpdated { add { } remove { } }
        public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;
        public Task<PreviewStartResult> StartAsync(PreviewStartRequest request) { IsActive = true; IsActiveChanged?.Invoke(this, EventArgs.Empty); return Task.FromResult(PreviewStartResult.Started(sessionId)); }
        public Task<PreviewStopResult> StopAsync(CancellationToken cancellationToken = default) { if (IsActive) order.Add("video-stop"); IsActive = false; IsActiveChanged?.Invoke(this, EventArgs.Empty); return Task.FromResult(PreviewStopResult.Stopped); }
        public void RaiseFirstFrame() => FirstFramePresented?.Invoke(this, new PreviewSessionEventArgs(sessionId));
        public void RaiseFailure() => PreviewFailed?.Invoke(this, new PreviewFailureEventArgs(sessionId, new PreviewFailure(PreviewFailureCategory.DeviceUnavailable, "Video failed.")));
        public void Dispose() { }
    }

    private sealed class FakeAudioMonitorService(List<string> order, AudioMonitorFailure? startFailure, bool stopTimeout) : IAudioMonitorService
    {
        private Guid? sessionId;
        public bool IsActive { get; private set; }
        public Guid? ActiveSessionId => sessionId;
        public int StartCalls { get; private set; }
        public double Volume { get; private set; } = 100;
        public bool Muted { get; private set; }
        public event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged;
        public event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated { add { } remove { } }
        public event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed;
        public Task<AudioMonitorStartResult> StartAsync(AudioMonitorStartRequest request, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            sessionId = Guid.NewGuid();
            Volume = request.InitialVolumePercent;
            Muted = request.InitiallyMuted;
            if (startFailure is not null) { IsActive = false; return Task.FromResult(AudioMonitorStartResult.Failed(sessionId.Value, startFailure)); }
            IsActive = true;
            StateChanged?.Invoke(this, new AudioMonitorStateChangedEventArgs(sessionId.Value, AudioMonitorState.Starting, Muted ? AudioMonitorState.Muted : AudioMonitorState.Monitoring));
            return Task.FromResult(AudioMonitorStartResult.Started(sessionId.Value));
        }
        public Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            if (IsActive) order.Add("audio-stop");
            if (stopTimeout)
                return Task.FromResult(AudioMonitorStopResult.Timeout(new AudioMonitorFailure(AudioMonitorFailureCategory.StopTimeout, "Audio stop timed out.")));
            IsActive = false;
            return Task.FromResult(AudioMonitorStopResult.Stopped);
        }
        public void SetVolume(double volumePercent) => Volume = volumePercent;
        public void SetMuted(bool muted) => Muted = muted;
        public void RaiseState(Guid id, AudioMonitorState value) => StateChanged?.Invoke(this, new AudioMonitorStateChangedEventArgs(id, AudioMonitorState.Monitoring, value));
        public void RaiseFailure(AudioMonitorFailure failure)
        {
            if (sessionId is Guid id) MonitoringFailed?.Invoke(this, new AudioMonitorFailureEventArgs(id, failure));
        }
        public void Dispose() { }
    }

    private sealed class FakeSurface : IPreviewSurface
    {
        public nint Handle => 42;
        public PreviewSurfaceSize PixelSize => new(1280, 720);
        public bool IsAvailable => true;
        public bool IsPresentable => true;
        public bool IsVideoVisible { get; private set; }
        public event EventHandler<PreviewSurfaceSize>? PixelSizeChanged { add { } remove { } }
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public event EventHandler? PresentabilityChanged { add { } remove { } }
        public void SetSurfaceActive(bool active) { }
        public void SetVideoVisible(bool visible) => IsVideoVisible = visible;
    }

    private sealed class FakeFullscreenController : IFullscreenWindowController
    {
        public bool IsFullscreen { get; private set; }
        public bool IsTransitioning => false;
        public long Generation { get; private set; }
        public event EventHandler<FullscreenControllerStateChangedEventArgs>? StateChanged;

        public Task<FullscreenTransitionResult> EnterAsync()
        {
            IsFullscreen = true;
            StateChanged?.Invoke(this, new FullscreenControllerStateChangedEventArgs(true, false, ++Generation));
            return Task.FromResult(FullscreenTransitionResult.Applied);
        }

        public Task<FullscreenTransitionResult> ExitAsync(FullscreenExitReason reason)
        {
            _ = reason;
            IsFullscreen = false;
            StateChanged?.Invoke(this, new FullscreenControllerStateChangedEventArgs(false, false, ++Generation));
            return Task.FromResult(FullscreenTransitionResult.Applied);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}

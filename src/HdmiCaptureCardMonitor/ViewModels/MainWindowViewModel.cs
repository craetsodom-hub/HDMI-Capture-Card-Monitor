using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Capture.Preview;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.Presentation.Fullscreen;
using HdmiCaptureCardMonitor.Presentation;

namespace HdmiCaptureCardMonitor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationLogger logger;
    private readonly CaptureSessionStateMachine stateMachine;
    private readonly ICaptureDeviceDiscoveryService discoveryService;
    private readonly ICapturePreviewService previewService;
    private readonly IAudioEndpointDiscoveryService audioDiscoveryService;
    private readonly IAudioMonitorService audioMonitorService;
    private readonly IPreviewSurface? previewSurface;
    private readonly IFullscreenWindowController? fullscreenController;
    private readonly SynchronizationContext? uiContext;
    private readonly object previewOperationSync = new();
    private readonly HashSet<Guid> retiredPreviewSessions = [];
    private CancellationTokenSource? deviceScanCancellation;
    private CancellationTokenSource? formatScanCancellation;
    private CancellationTokenSource? previewCancellation;
    private CancellationTokenSource? audioScanCancellation;
    private CancellationTokenSource? audioStartCancellation;
    private int deviceScanGeneration;
    private int formatScanGeneration;
    private int previewGeneration;
    private int audioScanGeneration;
    private Guid activePreviewSessionId;
    private Task formatDiscoveryCompletion = Task.CompletedTask;
    private Task? previewStopOperation;
    private bool hasPresentedFrame;
    private bool windowClosing;
    private bool disposed;

    public ObservableCollection<CaptureDevice> Devices { get; } = [];
    public ObservableCollection<NativeVideoCapability> Formats { get; } = [];
    public ObservableCollection<AudioEndpointChoice> AudioInputs { get; } = [];
    public ObservableCollection<AudioEndpointChoice> AudioOutputs { get; } = [];
    [ObservableProperty] private string statusMessage = "Scanning for video devices…";
    [ObservableProperty] private string previewTitle = "Scanning for video devices…";
    [ObservableProperty] private string previewDescription = "Please wait while Windows checks available video inputs.";
    [ObservableProperty] private bool isDeviceScanRunning;
    [ObservableProperty] private bool isFormatScanRunning;
    [ObservableProperty] private bool isPreviewMessageVisible = true;
    [ObservableProperty] private string previewGlyph = "\uE895";
    [ObservableProperty] private bool isInformationDialogOpen;
    [ObservableProperty] private string informationDialogEyebrow = string.Empty;
    [ObservableProperty] private string informationDialogTitle = string.Empty;
    [ObservableProperty] private string informationDialogDescription = string.Empty;
    [ObservableProperty] private string informationDialogDetails = string.Empty;
    [ObservableProperty] private CaptureDevice? selectedDevice;
    [ObservableProperty] private NativeVideoCapability? selectedFormat;
    [ObservableProperty] private PreviewDiagnostics? currentPreviewDiagnostics;
    [ObservableProperty] private AudioEndpointChoice? selectedAudioInput = AudioEndpointChoice.NoAudio;
    [ObservableProperty] private AudioEndpointChoice? selectedAudioOutput = AudioEndpointChoice.SystemDefaultOutput;
    [ObservableProperty] private bool isAudioScanRunning;
    [ObservableProperty] private AudioMonitorState audioMonitorState = AudioMonitorState.Off;
    [ObservableProperty] private string audioStatusText = "Audio off";
    [ObservableProperty] private double audioVolume = 100;
    [ObservableProperty] private bool isAudioMuted;
    [ObservableProperty] private AudioMonitorDiagnostics? currentAudioDiagnostics;

    public CaptureSessionState SessionState => stateMachine.CurrentState;
    public string SessionStateDisplay => GetSessionStateDisplay(SessionState);
    public bool IsPreviewActive => SessionState is CaptureSessionState.Starting or CaptureSessionState.Previewing or CaptureSessionState.Stopping;
    public bool IsMainContentEnabled => !IsInformationDialogOpen;
    public bool CanOpenInformationDialog => !IsInformationDialogOpen && !IsFullscreenPresentation && !IsFullscreenTransitioning && !windowClosing;
    public bool CanChangeCaptureSelection => !IsInformationDialogOpen && !IsPreviewActive && !IsDeviceScanRunning && !IsFormatScanRunning;
    public bool CanChangeAudioSelection => CanChangeCaptureSelection && !IsAudioScanRunning && !audioMonitorService.IsActive && !windowClosing;
    public bool CanAdjustAudio => AudioMonitorState is AudioMonitorState.Starting or AudioMonitorState.Monitoring or AudioMonitorState.Muted;
    public string MuteAccessText => IsAudioMuted ? "_Unmute" : "_Mute";
    public bool HasDevices => Devices.Count > 0 && CanChangeCaptureSelection;
    public bool HasFormats => Formats.Count > 0 && CanChangeCaptureSelection;
    public bool CanRefreshDevices => !IsInformationDialogOpen && !IsDeviceScanRunning && !IsAudioScanRunning && !IsPreviewActive;
    public string DevicePlaceholder => IsDeviceScanRunning ? "Scanning for video devices…" : Devices.Count > 0 ? "Select a capture device" : "No device available";
    public string FormatPlaceholder => SelectedDevice is null ? "Select a device first" : IsFormatScanRunning ? "Reading supported formats…" : Formats.Count > 0 ? "Select a native format" : "No native formats available";
    public bool CanStartCapture =>
        SessionState == CaptureSessionState.DeviceReady &&
        SelectedDevice is not null &&
        SelectedFormat is not null &&
        !IsInformationDialogOpen &&
        !IsDeviceScanRunning &&
        !IsFormatScanRunning &&
        previewSurface?.IsPresentable == true &&
        !audioMonitorService.IsActive &&
        !previewService.IsActive;
    public bool CanStartStopPreview => !IsInformationDialogOpen && (CanStartCapture || SessionState == CaptureSessionState.Previewing);
    public string StartStopText => SessionState switch
    {
        CaptureSessionState.Starting => "Starting…",
        CaptureSessionState.Previewing => "Stop",
        CaptureSessionState.Stopping => "Stopping…",
        _ => "Start"
    };
    public string StartStopAccessText => SessionState switch
    {
        CaptureSessionState.Starting => "Starting…",
        CaptureSessionState.Previewing => "_Stop",
        CaptureSessionState.Stopping => "Stopping…",
        _ => "_Start"
    };
    public bool IsFullscreen => fullscreenController?.IsFullscreen == true;
    public bool IsFullscreenTransitioning => fullscreenController?.IsTransitioning == true;
    public bool IsFullscreenPresentation => IsFullscreen;
    public bool IsWindowedChromeVisible => !IsFullscreenPresentation;
    public bool CanToggleFullscreen =>
        fullscreenController is not null &&
        !disposed &&
        !windowClosing &&
        !IsFullscreenTransitioning &&
        (IsFullscreen ||
         (SessionState == CaptureSessionState.Previewing &&
          hasPresentedFrame &&
          !IsInformationDialogOpen &&
          previewSurface?.IsAvailable == true &&
          previewSurface.IsPresentable));
    public bool CanFullscreen => CanToggleFullscreen;
    public string FullscreenAccessText => IsFullscreen ? "Exit fullscreen" : "_Fullscreen";
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This remains an instance-bound MVVM contract for a deferred command.")]
    public bool CanTakeSnapshot => false;
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This remains an instance-bound MVVM contract for a deferred command.")]
    public bool CanRecord => false;
    public string PreviewControlHint => SessionState switch
    {
        CaptureSessionState.Starting => "Preparing the GPU preview.",
        CaptureSessionState.Previewing => "Live preview is active.",
        CaptureSessionState.Stopping => "Releasing the video device safely.",
        CaptureSessionState.Faulted => "Review the message above, correct the issue, and try again.",
        CaptureSessionState.Enumerating when SelectedDevice is not null => "Reading the device’s native video formats.",
        CaptureSessionState.Enumerating => "Scanning for available video devices.",
        CaptureSessionState.DeviceReady when SelectedFormat is null => "Select a native format to enable preview.",
        CaptureSessionState.DeviceReady when PreviewTitle == "Preview stopped" => "Select Start to resume preview.",
        CaptureSessionState.DeviceReady => "Ready to open the selected video input.",
        _ when SelectedDevice is null => "Select a capture device to continue.",
        _ when IsFormatScanRunning => "Reading the device’s native video formats.",
        _ when SelectedFormat is null => "Select a native format to enable preview.",
        _ => "Ready to open the selected video input."
    };
    internal Task FormatDiscoveryCompletion => formatDiscoveryCompletion;

    public MainWindowViewModel(
        IApplicationLogger logger,
        string? startupNotice = null,
        CaptureSessionStateMachine? stateMachine = null,
        ICaptureDeviceDiscoveryService? discoveryService = null,
        ICapturePreviewService? previewService = null,
        IPreviewSurface? previewSurface = null,
        IFullscreenWindowController? fullscreenController = null,
        SynchronizationContext? synchronizationContext = null,
        IAudioEndpointDiscoveryService? audioDiscoveryService = null,
        IAudioMonitorService? audioMonitorService = null)
    {
        this.logger = logger;
        this.discoveryService = discoveryService ?? new UnavailableCaptureDeviceDiscoveryService("Video device discovery is unavailable.");
        this.previewService = previewService ?? new UnavailableCapturePreviewService(new PreviewFailure(PreviewFailureCategory.DeviceUnavailable, "Live preview is unavailable."));
        this.audioDiscoveryService = audioDiscoveryService ?? new UnavailableAudioEndpointDiscoveryService();
        this.audioMonitorService = audioMonitorService ?? new UnavailableAudioMonitorService();
        this.previewSurface = previewSurface;
        this.fullscreenController = fullscreenController;
        this.stateMachine = stateMachine ?? new CaptureSessionStateMachine();
        uiContext = synchronizationContext ?? SynchronizationContext.Current;
        this.stateMachine.StateChanged += OnStateChanged;
        this.previewService.IsActiveChanged += OnPreviewServiceIsActiveChanged;
        this.previewService.FirstFramePresented += OnFirstFramePresented;
        this.previewService.DiagnosticsUpdated += OnDiagnosticsUpdated;
        this.previewService.PreviewFailed += OnPreviewFailed;
        this.audioMonitorService.StateChanged += OnAudioStateChanged;
        this.audioMonitorService.DiagnosticsUpdated += OnAudioDiagnosticsUpdated;
        this.audioMonitorService.MonitoringFailed += OnAudioMonitoringFailed;
        if (this.fullscreenController is not null) this.fullscreenController.StateChanged += OnFullscreenControllerStateChanged;
        if (this.previewSurface is not null)
        {
            this.previewSurface.AvailabilityChanged += OnSurfaceAvailabilityChanged;
            this.previewSurface.PresentabilityChanged += OnSurfaceAvailabilityChanged;
        }
        if (!string.IsNullOrWhiteSpace(startupNotice)) StatusMessage = startupNotice;
        AudioInputs.Add(AudioEndpointChoice.NoAudio);
        AudioOutputs.Add(AudioEndpointChoice.SystemDefaultOutput);
    }

    public void StartInitialDiscovery() => _ = RefreshDevicesAsync();
    internal Task RefreshDevicesForTestingAsync() => RefreshDevicesAsync();
    internal Task StartPreviewForTestingAsync() => StartPreviewAsync();
    internal Task StopPreviewForTestingAsync() => StopPreviewAsync();
    internal Guid ActivePreviewSessionId => activePreviewSessionId;
    internal nint PreviewSurfaceHandle => previewSurface?.Handle ?? 0;

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        var audioRefresh = RefreshAudioEndpointsAsync();
        await RefreshVideoDevicesAsync();
        await audioRefresh;
    }

    private async Task RefreshVideoDevicesAsync()
    {
        RetirePreviewSession(activePreviewSessionId);
        activePreviewSessionId = Guid.Empty;
        Interlocked.Increment(ref previewGeneration);
        var generation = Interlocked.Increment(ref deviceScanGeneration);
        var localCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref deviceScanCancellation, localCancellation);
        previous?.Cancel();
        previous?.Dispose();
        var token = localCancellation.Token;

        CancelFormats();
        Devices.Clear();
        Formats.Clear();
        SelectedDevice = null;
        SelectedFormat = null;
        NotifyCaptureProperties();
        IsDeviceScanRunning = true;
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        TryTransition(CaptureSessionState.Enumerating);
        StatusMessage = "Scanning for video devices…";
        PreviewGlyph = "\uE721";
        PreviewTitle = StatusMessage;
        PreviewDescription = "Please wait while Windows checks available video inputs.";

        try
        {
            var result = await discoveryService.EnumerateVideoDevicesAsync(token);
            if (!IsCurrentDeviceRequest(generation) || disposed) return;
            if (result.IsCancelled)
            {
                ApplyCurrentDeviceCancellation();
                return;
            }
            if (!result.IsSuccess)
            {
                ApplyDiscoveryFailureMessage(result.Failure!);
                TryTransition(CaptureSessionState.Faulted);
                return;
            }

            foreach (var device in result.Value ?? []) Devices.Add(device);
            NotifyCaptureProperties();
            if (Devices.Count == 0)
            {
                StatusMessage = "No compatible video capture devices found.";
                PreviewGlyph = "\uE711";
                PreviewTitle = "No capture device detected";
                PreviewDescription = "Connect a compatible USB HDMI capture card and select Refresh.";
            }
            else ApplySelectDeviceState();
            TryTransition(CaptureSessionState.Idle);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentDeviceRequest(generation) && !disposed) ApplyCurrentDeviceCancellation();
        }
        catch (Exception exception)
        {
            if (!IsCurrentDeviceRequest(generation) || disposed) return;
            logger.LogError("Device enumeration failed.", exception);
            ApplyDiscoveryFailureMessage(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, DiscoveryFailureCategory.Unknown, null, "Device enumeration failed.", exception));
            TryTransition(CaptureSessionState.Faulted);
        }
        finally
        {
            if (IsCurrentDeviceRequest(generation) && !disposed)
            {
                IsDeviceScanRunning = false;
                NotifyCaptureProperties();
            }
        }
    }

    private async Task RefreshAudioEndpointsAsync()
    {
        var generation = Interlocked.Increment(ref audioScanGeneration);
        var localCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref audioScanCancellation, localCancellation);
        previous?.Cancel();
        previous?.Dispose();
        var selectedInputId = SelectedAudioInput?.Endpoint?.Id;
        var selectedOutputId = SelectedAudioOutput?.Endpoint?.Id;
        IsAudioScanRunning = true;
        try
        {
            var result = await audioDiscoveryService.EnumerateActiveEndpointsAsync(localCancellation.Token);
            if (disposed || generation != Volatile.Read(ref audioScanGeneration)) return;
            AudioInputs.Clear();
            AudioOutputs.Clear();
            AudioInputs.Add(AudioEndpointChoice.NoAudio);
            AudioOutputs.Add(AudioEndpointChoice.SystemDefaultOutput);
            if (!result.IsSuccess)
            {
                SelectedAudioInput = AudioEndpointChoice.NoAudio;
                SelectedAudioOutput = AudioEndpointChoice.SystemDefaultOutput;
                AudioMonitorState = AudioMonitorState.Faulted;
                AudioStatusText = "Audio unavailable";
                SafeLogWarning($"Audio endpoint discovery failed safely with {result.Failure!.Category}.");
                return;
            }

            foreach (var endpoint in result.CaptureEndpoints)
                AudioInputs.Add(new AudioEndpointChoice(endpoint.DisplayName, endpoint));
            foreach (var endpoint in result.RenderEndpoints)
                AudioOutputs.Add(new AudioEndpointChoice(endpoint.DisplayName, endpoint));
            SelectedAudioInput = AudioInputs.FirstOrDefault(choice => choice.Endpoint?.Id == selectedInputId) ?? AudioEndpointChoice.NoAudio;
            SelectedAudioOutput = AudioOutputs.FirstOrDefault(choice => choice.Endpoint?.Id == selectedOutputId) ?? AudioEndpointChoice.SystemDefaultOutput;
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (disposed || generation != Volatile.Read(ref audioScanGeneration)) return;
            AudioMonitorState = AudioMonitorState.Faulted;
            AudioStatusText = "Audio unavailable";
            SafeLogError("Audio endpoint discovery failed safely.", exception);
        }
        finally
        {
            if (!disposed && generation == Volatile.Read(ref audioScanGeneration)) IsAudioScanRunning = false;
        }
    }

    partial void OnSelectedDeviceChanged(CaptureDevice? value)
    {
        formatDiscoveryCompletion = LoadFormatsAsync(value);
        NotifyCaptureProperties();
    }

    partial void OnSelectedFormatChanged(NativeVideoCapability? value)
    {
        if (value is not null) RecoverReadyStateAfterCleanup();
        NotifyCaptureProperties();
    }

    partial void OnIsDeviceScanRunningChanged(bool value) { _ = value; NotifyCaptureProperties(); }
    partial void OnIsFormatScanRunningChanged(bool value) { _ = value; NotifyCaptureProperties(); }
    partial void OnIsAudioScanRunningChanged(bool value) { _ = value; NotifyAudioProperties(); NotifyCaptureProperties(); }
    partial void OnSelectedAudioInputChanged(AudioEndpointChoice? value)
    {
        AudioStatusText = value?.Endpoint is null ? "Audio off" : "Waiting for live video";
        AudioMonitorState = value?.Endpoint is null ? AudioMonitorState.Off : AudioMonitorState.WaitingForVideo;
        NotifyAudioProperties();
    }
    partial void OnSelectedAudioOutputChanged(AudioEndpointChoice? value) { _ = value; NotifyAudioProperties(); }
    partial void OnAudioVolumeChanged(double value)
    {
        var clamped = AudioGainController.ClampVolumePercent(value);
        if (!value.Equals(clamped)) { AudioVolume = clamped; return; }
        audioMonitorService.SetVolume(clamped);
    }
    partial void OnIsAudioMutedChanged(bool value)
    {
        audioMonitorService.SetMuted(value);
        OnPropertyChanged(nameof(MuteAccessText));
    }
    partial void OnPreviewTitleChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(PreviewControlHint));
    }

    partial void OnIsInformationDialogOpenChanged(bool value)
    {
        _ = value;
        NotifyCaptureProperties();
        ShowSettingsInformationCommand.NotifyCanExecuteChanged();
        ShowHelpInformationCommand.NotifyCanExecuteChanged();
        CloseInformationDialogCommand.NotifyCanExecuteChanged();
        UpdatePreviewSurfacePresentation();
    }

    private async Task LoadFormatsAsync(CaptureDevice? device)
    {
        var generation = Interlocked.Increment(ref formatScanGeneration);
        CancelFormats();
        Formats.Clear();
        SelectedFormat = null;
        NotifyCaptureProperties();

        if (device is null)
        {
            if (SessionState is CaptureSessionState.DeviceReady or CaptureSessionState.Enumerating) TryTransition(CaptureSessionState.Idle);
            ApplySelectDeviceState();
            return;
        }

        var localCancellation = new CancellationTokenSource();
        formatScanCancellation = localCancellation;
        var token = localCancellation.Token;
        IsFormatScanRunning = true;
        TryTransition(CaptureSessionState.Enumerating);
        StatusMessage = "Reading supported formats…";
        PreviewGlyph = "\uE9D9";
        PreviewTitle = StatusMessage;
        PreviewDescription = "The selected device is being inspected.";

        try
        {
            var result = await discoveryService.GetNativeVideoCapabilitiesAsync(device, token);
            if (!IsCurrentFormatRequest(generation, device) || disposed) return;
            if (result.IsCancelled)
            {
                ApplyCurrentFormatCancellation();
                return;
            }
            if (!result.IsSuccess)
            {
                ApplyDiscoveryFailureMessage(result.Failure!);
                TryTransition(CaptureSessionState.Faulted);
                return;
            }

            foreach (var format in result.Value ?? []) Formats.Add(format);
            NotifyCaptureProperties();
            if (Formats.Count == 0)
            {
                ApplyDiscoveryFailureMessage(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, DiscoveryFailureCategory.NoUsableFormats, null, "A successful capability result contained no formats."));
                TryTransition(CaptureSessionState.Faulted);
                return;
            }

            StatusMessage = $"Device ready · {Formats.Count} formats available";
            PreviewGlyph = "\uE73E";
            PreviewTitle = "Capture device ready";
            PreviewDescription = "Select a native video format below.";
            TryTransition(CaptureSessionState.DeviceReady);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentFormatRequest(generation, device) && !disposed) ApplyCurrentFormatCancellation();
        }
        catch (Exception exception)
        {
            if (!IsCurrentFormatRequest(generation, device) || disposed) return;
            logger.LogError("Native format discovery failed.", exception);
            ApplyDiscoveryFailureMessage(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, DiscoveryFailureCategory.Unknown, null, "Native format discovery failed.", exception));
            TryTransition(CaptureSessionState.Faulted);
        }
        finally
        {
            if (IsCurrentFormatRequest(generation, device) && !disposed) IsFormatScanRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartStopPreview))]
    private async Task TogglePreviewAsync()
    {
        if (SessionState == CaptureSessionState.Previewing)
        {
            await StopPreviewAsync();
        }
        else await StartPreviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAdjustAudio))]
    private void ToggleAudioMute() => IsAudioMuted = !IsAudioMuted;

    [RelayCommand(CanExecute = nameof(CanToggleFullscreen))]
    private async Task ToggleFullscreenAsync()
    {
        if (fullscreenController is null || disposed || windowClosing || IsFullscreenTransitioning) return;
        var entering = !IsFullscreen;
        var started = Stopwatch.GetTimestamp();
        SafeLogInformation(
            $"Fullscreen {(entering ? "entry" : "exit")} requested for preview session {activePreviewSessionId:N} " +
            $"and surface HWND 0x{PreviewSurfaceHandle:X}.");
        FullscreenTransitionResult result;
        try
        {
            result = entering
                ? await fullscreenController.EnterAsync()
                : await fullscreenController.ExitAsync(FullscreenExitReason.User);
        }
        catch (Exception exception)
        {
            result = FullscreenTransitionResult.Failed(FullscreenFailure.Unexpected(
                entering ? FullscreenOperation.Entry : FullscreenOperation.ExactRestore,
                exception));
        }

        ApplyFullscreenTransitionResult(result, showFailureMessage: true);
        SafeLogInformation(
            $"Fullscreen {(entering ? "entry" : "exit")} completed in " +
            $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms with {result.Disposition}; " +
            $"preview session {activePreviewSessionId:N}, surface HWND 0x{PreviewSurfaceHandle:X}.");
    }

    internal async Task ExitFullscreenForWindowAsync(FullscreenExitReason reason)
    {
        await ExitFullscreenPresentationAsync(reason, showFailureMessage: false);
    }

    internal void SetWindowClosing()
    {
        if (windowClosing) return;
        windowClosing = true;
        NotifyFullscreenProperties();
    }

    private async Task StartPreviewAsync()
    {
        if (!CanStartCapture || disposed || SelectedDevice is null || SelectedFormat is null || previewSurface is null) return;
        var generation = Interlocked.Increment(ref previewGeneration);
        var localCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref previewCancellation, localCancellation);
        previous?.Cancel();
        previous?.Dispose();
        activePreviewSessionId = Guid.Empty;
        hasPresentedFrame = false;
        CurrentPreviewDiagnostics = null;
        CurrentAudioDiagnostics = null;
        AudioMonitorState = SelectedAudioInput?.Endpoint is null ? AudioMonitorState.Off : AudioMonitorState.WaitingForVideo;
        AudioStatusText = SelectedAudioInput?.Endpoint is null ? "Audio off" : "Waiting for live video";
        NotifyAudioProperties();
        IsPreviewMessageVisible = true;
        TryTransition(CaptureSessionState.Starting);
        StatusMessage = "Starting live preview…";
        PreviewGlyph = "\uE768";
        PreviewTitle = "Starting preview…";
        PreviewDescription = "Opening the selected video mode and preparing GPU rendering.";

        var request = new PreviewStartRequest(SelectedDevice, SelectedFormat, previewSurface, localCancellation.Token);
        PreviewStartResult result;
        try
        {
            result = await previewService.StartAsync(request);
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            if (!IsCurrentPreviewOperation(generation) || disposed) return;
            logger.LogError("Preview startup failed unexpectedly.", exception);
            ApplyPreviewFailureMessage(new PreviewFailure(PreviewFailureCategory.Unknown, "The live preview could not start.", null, exception));
            TryTransition(CaptureSessionState.Faulted);
            return;
        }

        if (!IsCurrentPreviewOperation(generation) || disposed) return;
        if (result.IsCancelled)
        {
            IsPreviewMessageVisible = true;
            TryTransition(CaptureSessionState.DeviceReady);
            StatusMessage = "Preview start was cancelled.";
            PreviewGlyph = "\uE71A";
            PreviewTitle = "Preview stopped";
            PreviewDescription = "Select Start to open the selected video input.";
            return;
        }
        if (!result.IsSuccess)
        {
            RetirePreviewSession(result.SessionId);
            activePreviewSessionId = Guid.Empty;
            ApplyPreviewFailureMessage(result.Failure!);
            TryTransition(CaptureSessionState.Faulted);
            if (result.Failure?.Category != PreviewFailureCategory.ShutdownTimeout)
                RecoverReadyStateAfterCleanup();
            return;
        }

        if (activePreviewSessionId == Guid.Empty) activePreviewSessionId = result.SessionId;
    }

    private async Task StopPreviewAsync()
    {
        await ExitFullscreenPresentationAsync(FullscreenExitReason.Stop, showFailureMessage: false);
        Task operation;
        lock (previewOperationSync)
        {
            if (disposed || SessionState is not (CaptureSessionState.Starting or CaptureSessionState.Previewing or CaptureSessionState.Stopping)) return;
            operation = previewStopOperation ??= StopPreviewAndResetAsync();
        }
        await operation;
    }

    private async Task StopPreviewAndResetAsync()
    {
        try { await StopPreviewCoreAsync(); }
        finally { lock (previewOperationSync) previewStopOperation = null; }
    }

    private async Task StopPreviewCoreAsync()
    {
        var generation = Interlocked.Increment(ref previewGeneration);
        hasPresentedFrame = false;
        NotifyFullscreenProperties();
        var cancellation = Interlocked.Exchange(ref previewCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (SessionState != CaptureSessionState.Stopping) TryTransition(CaptureSessionState.Stopping);
        StatusMessage = "Stopping live preview…";
        PreviewGlyph = "\uE71A";
        PreviewTitle = "Stopping preview…";
        PreviewDescription = "Releasing the video device and graphics resources safely.";

        await StopAudioSafelyAsync();

        PreviewStopResult result;
        try { result = await previewService.StopAsync(); }
        catch (Exception exception)
        {
            result = PreviewStopResult.Failed(new PreviewFailure(PreviewFailureCategory.Unknown, "The live preview could not stop cleanly.", null, exception));
        }

        if (!IsCurrentPreviewOperation(generation) || disposed) return;
        IsPreviewMessageVisible = true;
        RetirePreviewSession(activePreviewSessionId);
        activePreviewSessionId = Guid.Empty;
        if (!result.IsSuccess)
        {
            ApplyPreviewFailureMessage(result.Failure!);
            TryTransition(CaptureSessionState.Faulted);
            return;
        }

        TryTransition(SelectedDevice is not null && SelectedFormat is not null ? CaptureSessionState.DeviceReady : CaptureSessionState.Idle);
        StatusMessage = "Preview stopped.";
        PreviewGlyph = "\uE71A";
        PreviewTitle = "Preview stopped";
        PreviewDescription = "Select Start to resume the selected video input.";
    }

    [RelayCommand(CanExecute = nameof(CanOpenInformationDialog))]
    private void ShowSettingsInformation()
    {
        InformationDialogEyebrow = "SETTINGS";
        InformationDialogTitle = "Settings are coming later";
        InformationDialogDescription = "This release keeps capture behavior explicit and predictable, so there are no placeholder switches or controls that pretend to change the application.";
        InformationDialogDetails = "Device and native-format selection are available in the main monitor. Additional local preferences will be introduced only when they are fully implemented and tested.";
        IsInformationDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanOpenInformationDialog))]
    private void ShowHelpInformation()
    {
        InformationDialogEyebrow = "HELP";
        InformationDialogTitle = "Start a live monitor preview";
        InformationDialogDescription = "Connect a compatible video capture device, select Refresh, choose the device and one of its native formats, then select Start. Audio monitoring is optional; select No audio to keep it off. Fullscreen requires an active live preview.";
        InformationDialogDetails = "For audio, choose an input and System default output or a named output before starting; use headphones when monitoring a microphone to prevent feedback. Press F11 to enter or exit fullscreen, or Escape to leave it. Capture remains active while the window changes presentation, and audio does not restart. If access is denied, enable camera access and microphone access for desktop apps in Windows Privacy & security. Physical USB HDMI capture-card video and audio compatibility remain required release tests.";
        IsInformationDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(IsInformationDialogOpen))]
    private void CloseInformationDialog() => IsInformationDialogOpen = false;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hasPresentedFrame = false;
        windowClosing = true;
        UpdatePreviewSurfacePresentation();
        Interlocked.Increment(ref deviceScanGeneration);
        Interlocked.Increment(ref formatScanGeneration);
        Interlocked.Increment(ref previewGeneration);
        previewService.FirstFramePresented -= OnFirstFramePresented;
        previewService.DiagnosticsUpdated -= OnDiagnosticsUpdated;
        previewService.PreviewFailed -= OnPreviewFailed;
        previewService.IsActiveChanged -= OnPreviewServiceIsActiveChanged;
        if (fullscreenController is not null) fullscreenController.StateChanged -= OnFullscreenControllerStateChanged;
        if (previewSurface is not null)
        {
            previewSurface.AvailabilityChanged -= OnSurfaceAvailabilityChanged;
            previewSurface.PresentabilityChanged -= OnSurfaceAvailabilityChanged;
        }
        CancelFormats();
        var deviceCancellation = Interlocked.Exchange(ref deviceScanCancellation, null);
        deviceCancellation?.Cancel();
        deviceCancellation?.Dispose();
        var activeCancellation = Interlocked.Exchange(ref previewCancellation, null);
        activeCancellation?.Cancel();
        activeCancellation?.Dispose();
        var audioScan = Interlocked.Exchange(ref audioScanCancellation, null);
        audioScan?.Cancel();
        audioScan?.Dispose();
        var audioStart = Interlocked.Exchange(ref audioStartCancellation, null);
        audioStart?.Cancel();
        audioStart?.Dispose();
        audioMonitorService.StateChanged -= OnAudioStateChanged;
        audioMonitorService.DiagnosticsUpdated -= OnAudioDiagnosticsUpdated;
        audioMonitorService.MonitoringFailed -= OnAudioMonitoringFailed;
        try { _ = audioMonitorService.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { SafeLogError("Audio shutdown failed safely during window disposal.", exception); }
        try { _ = previewService.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { SafeLogError("Preview shutdown failed safely during window disposal.", exception); }
        stateMachine.StateChanged -= OnStateChanged;
    }

    private void OnFirstFramePresented(object? sender, PreviewSessionEventArgs e) => PostToUi(() =>
    {
        if (disposed || SessionState != CaptureSessionState.Starting) return;
        if (IsRetiredPreviewSession(e.SessionId)) return;
        if (activePreviewSessionId != Guid.Empty && activePreviewSessionId != e.SessionId) return;
        activePreviewSessionId = e.SessionId;
        hasPresentedFrame = true;
        IsPreviewMessageVisible = false;
        TryTransition(CaptureSessionState.Previewing);
        StatusMessage = "Previewing live video.";
        if (SelectedAudioInput?.Endpoint is not null) _ = StartAudioAfterFirstFrameAsync(e.SessionId);
    });

    private async Task StartAudioAfterFirstFrameAsync(Guid videoSessionId)
    {
        var input = SelectedAudioInput?.Endpoint;
        if (input is null || disposed || videoSessionId != activePreviewSessionId || SessionState != CaptureSessionState.Previewing) return;
        var localCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref audioStartCancellation, localCancellation);
        previous?.Cancel();
        previous?.Dispose();
        AudioMonitorState = AudioMonitorState.Starting;
        AudioStatusText = "Starting audio…";
        NotifyAudioProperties();
        AudioMonitorStartResult result;
        try
        {
            result = await audioMonitorService.StartAsync(new AudioMonitorStartRequest(
                input,
                SelectedAudioOutput?.Endpoint,
                AudioVolume,
                IsAudioMuted), localCancellation.Token);
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            result = AudioMonitorStartResult.Failed(Guid.Empty,
                new AudioMonitorFailure(AudioMonitorFailureCategory.OtherFailure, "Audio monitoring could not start.", null, exception));
        }

        PostToUi(() =>
        {
            if (disposed || videoSessionId != activePreviewSessionId || SessionState != CaptureSessionState.Previewing) return;
            if (result.IsSuccess) return;
            AudioMonitorState = AudioMonitorState.Faulted;
            AudioStatusText = "Audio unavailable";
            StatusMessage = $"Video is live. Audio monitoring could not start. {result.Failure?.CustomerMessage}".Trim();
            NotifyAudioProperties();
        });
    }

    private async Task StopAudioSafelyAsync()
    {
        var cancellation = Interlocked.Exchange(ref audioStartCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (!audioMonitorService.IsActive)
        {
            AudioMonitorState = AudioMonitorState.Off;
            AudioStatusText = "Audio off";
            NotifyAudioProperties();
            return;
        }

        AudioMonitorState = AudioMonitorState.Stopping;
        AudioStatusText = "Stopping audio…";
        NotifyAudioProperties();
        try
        {
            var result = await audioMonitorService.StopAsync();
            if (!result.IsSuccess)
            {
                SafeLogWarning($"Audio stop completed with {result.Failure?.Category}; video cleanup continued.");
                if (result.TimedOut && audioMonitorService.IsActive)
                {
                    AudioMonitorState = AudioMonitorState.Faulted;
                    AudioStatusText = "Audio cleanup timed out";
                    NotifyAudioProperties();
                    return;
                }
            }
        }
        catch (Exception exception) { SafeLogError("Audio stop failed safely; video cleanup continued.", exception); }
        AudioMonitorState = AudioMonitorState.Off;
        AudioStatusText = "Audio off";
        CurrentAudioDiagnostics = null;
        NotifyAudioProperties();
    }

    private void OnAudioStateChanged(object? sender, AudioMonitorStateChangedEventArgs e) => PostToUi(() =>
    {
        if (disposed || audioMonitorService.ActiveSessionId != e.SessionId) return;
        AudioMonitorState = e.CurrentState;
        AudioStatusText = e.CurrentState switch
        {
            AudioMonitorState.WaitingForVideo => "Waiting for live video",
            AudioMonitorState.Starting => "Starting audio…",
            AudioMonitorState.Monitoring => "Monitoring audio",
            AudioMonitorState.Muted => "Muted",
            AudioMonitorState.Stopping => "Stopping audio…",
            AudioMonitorState.Faulted => "Audio unavailable",
            _ => "Audio off"
        };
        NotifyAudioProperties();
    });

    private void OnAudioDiagnosticsUpdated(object? sender, AudioMonitorDiagnosticsEventArgs e) => PostToUi(() =>
    {
        if (disposed || audioMonitorService.ActiveSessionId != e.SessionId) return;
        CurrentAudioDiagnostics = e.Diagnostics;
    });

    private void OnAudioMonitoringFailed(object? sender, AudioMonitorFailureEventArgs e) => PostToUi(() =>
    {
        if (disposed || audioMonitorService.ActiveSessionId != e.SessionId) return;
        AudioMonitorState = AudioMonitorState.Faulted;
        AudioStatusText = e.Failure.Category is AudioMonitorFailureCategory.DeviceInvalidated or AudioMonitorFailureCategory.ResourcesInvalidated
            ? "Audio device disconnected"
            : "Audio unavailable";
        if (SessionState == CaptureSessionState.Previewing)
            StatusMessage = $"Video is live. Audio monitoring could not start. {e.Failure.CustomerMessage}";
        SafeLogWarning($"Audio monitoring failed safely with {e.Failure.Category}; video remained active.");
        NotifyAudioProperties();
    });

    private void OnDiagnosticsUpdated(object? sender, PreviewDiagnosticsEventArgs e) => PostToUi(() =>
    {
        if (disposed || activePreviewSessionId != e.SessionId) return;
        CurrentPreviewDiagnostics = e.Diagnostics;
        if (SessionState == CaptureSessionState.Previewing && AudioMonitorState != AudioMonitorState.Faulted)
            StatusMessage = $"Previewing · {e.Diagnostics.FramesReceivedPerSecond:0.0} received / {e.Diagnostics.RenderedFramesPerSecond:0.0} rendered fps";
    });

    private void OnPreviewFailed(object? sender, PreviewFailureEventArgs e) => PostToUi(() =>
    {
        _ = HandleRuntimePreviewFailureAsync(e);
    });

    private async Task HandleRuntimePreviewFailureAsync(PreviewFailureEventArgs e)
    {
        try
        {
            if (disposed || IsDeviceScanRunning || IsRetiredPreviewSession(e.SessionId)) return;
            if (SessionState is not (CaptureSessionState.Starting or CaptureSessionState.Previewing or CaptureSessionState.Stopping)) return;
            // Startup failures are returned by StartAsync after that session's cleanup.
            // Runtime events are accepted only for the session already bound here, so a
            // queued event from an older Starting session cannot fault a newer attempt.
            if (activePreviewSessionId == Guid.Empty || activePreviewSessionId != e.SessionId) return;

            await ExitFullscreenPresentationAsync(FullscreenExitReason.PreviewFailure, showFailureMessage: false);
            if (disposed || activePreviewSessionId != e.SessionId) return;

            await StopAudioSafelyAsync();

            var generation = Interlocked.Increment(ref previewGeneration);
            activePreviewSessionId = e.SessionId;
            hasPresentedFrame = false;
            IsPreviewMessageVisible = true;
            ApplyPreviewFailureMessage(e.Failure);
            TryTransition(CaptureSessionState.Faulted);
            await CleanupFailedPreviewAsync(e.SessionId, generation);
        }
        catch (Exception exception)
        {
            SafeLogError("Preview failure handling completed with a contained exception.", exception);
        }
    }

    private async Task CleanupFailedPreviewAsync(Guid failedSessionId, int generation)
    {
        PreviewStopResult result;
        try { result = await previewService.StopAsync(); }
        catch (Exception exception)
        {
            logger.LogError("Preview cleanup after failure did not complete.", exception);
            result = PreviewStopResult.Failed(new PreviewFailure(PreviewFailureCategory.Unknown, "Preview cleanup failed.", null, exception));
        }

        PostToUi(() =>
        {
            if (disposed || generation != Volatile.Read(ref previewGeneration) || activePreviewSessionId != failedSessionId) return;
            RetirePreviewSession(failedSessionId);
            activePreviewSessionId = Guid.Empty;
            IsPreviewMessageVisible = true;
            NotifyCaptureProperties();
            if (result.IsSuccess)
            {
                RecoverReadyStateAfterCleanup();
                return;
            }

            ApplyPreviewFailureMessage(result.Failure ?? new PreviewFailure(PreviewFailureCategory.Unknown, "Preview cleanup failed."));
            TryTransition(CaptureSessionState.Faulted);
        });
    }

    private void OnPreviewServiceIsActiveChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        PostToUi(NotifyCaptureProperties);
    }

    private void OnSurfaceAvailabilityChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        PostToUi(() =>
        {
            NotifyCaptureProperties();
            UpdatePreviewSurfacePresentation();
        });
    }

    private void OnFullscreenControllerStateChanged(object? sender, FullscreenControllerStateChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        PostToUi(NotifyFullscreenProperties);
    }

    private async Task ExitFullscreenPresentationAsync(FullscreenExitReason reason, bool showFailureMessage)
    {
        if (fullscreenController is null || (!fullscreenController.IsFullscreen && !fullscreenController.IsTransitioning)) return;
        var started = Stopwatch.GetTimestamp();
        SafeLogInformation(
            $"Fullscreen exit ({reason}) requested for preview session {activePreviewSessionId:N} " +
            $"and surface HWND 0x{PreviewSurfaceHandle:X}.");
        FullscreenTransitionResult result;
        try { result = await fullscreenController.ExitAsync(reason); }
        catch (Exception exception)
        {
            result = FullscreenTransitionResult.Failed(FullscreenFailure.Unexpected(
                reason == FullscreenExitReason.Disposal ? FullscreenOperation.Disposal : FullscreenOperation.ExactRestore,
                exception));
        }
        ApplyFullscreenTransitionResult(result, showFailureMessage);
        SafeLogInformation(
            $"Fullscreen exit ({reason}) completed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms " +
            $"with {result.Disposition}; preview session {activePreviewSessionId:N}, " +
            $"surface HWND 0x{PreviewSurfaceHandle:X}.");
    }

    private void ApplyFullscreenTransitionResult(FullscreenTransitionResult result, bool showFailureMessage)
    {
        if (!result.IsSuccess && result.Failure is not null)
        {
            var nativeDetail = result.Failure.NativeError is uint nativeError
                ? $" Native Win32 error: {nativeError}."
                : string.Empty;
            var technicalMessage = result.Failure.TechnicalMessage + nativeDetail;
            SafeLogError(technicalMessage, result.Failure.Exception ?? new InvalidOperationException(technicalMessage));
            if (showFailureMessage) StatusMessage = result.Failure.CustomerMessage;
        }
        NotifyFullscreenProperties();
    }

    private void PostToUi(Action action)
    {
        if (uiContext is null || SynchronizationContext.Current == uiContext) action();
        else uiContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    private void CancelFormats()
    {
        var previous = Interlocked.Exchange(ref formatScanCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private bool IsCurrentDeviceRequest(int generation) => generation == Volatile.Read(ref deviceScanGeneration);
    private bool IsCurrentFormatRequest(int generation, CaptureDevice device) => generation == Volatile.Read(ref formatScanGeneration) && ReferenceEquals(device, SelectedDevice);
    private bool IsCurrentPreviewOperation(int generation) => generation == Volatile.Read(ref previewGeneration);

    private void RetirePreviewSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return;
        lock (previewOperationSync) retiredPreviewSessions.Add(sessionId);
    }

    private bool IsRetiredPreviewSession(Guid sessionId)
    {
        lock (previewOperationSync) return retiredPreviewSessions.Contains(sessionId);
    }

    private void SafeLogError(string message, Exception exception)
    {
        try { logger.LogError(message, exception); }
        catch (Exception) { }
    }

    private void RecoverReadyStateAfterCleanup()
    {
        if (disposed || previewService.IsActive || SelectedDevice is null || SelectedFormat is null) return;
        if (SessionState == CaptureSessionState.Faulted) TryTransition(CaptureSessionState.DeviceReady);
    }

    private void NotifyCaptureProperties()
    {
        OnPropertyChanged(nameof(IsPreviewActive));
        OnPropertyChanged(nameof(IsMainContentEnabled));
        OnPropertyChanged(nameof(CanOpenInformationDialog));
        OnPropertyChanged(nameof(CanChangeCaptureSelection));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasFormats));
        OnPropertyChanged(nameof(CanRefreshDevices));
        OnPropertyChanged(nameof(DevicePlaceholder));
        OnPropertyChanged(nameof(FormatPlaceholder));
        OnPropertyChanged(nameof(CanStartCapture));
        OnPropertyChanged(nameof(CanStartStopPreview));
        OnPropertyChanged(nameof(StartStopText));
        OnPropertyChanged(nameof(StartStopAccessText));
        OnPropertyChanged(nameof(PreviewControlHint));
        NotifyFullscreenProperties();
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
        NotifyAudioProperties();
    }

    private void NotifyAudioProperties()
    {
        OnPropertyChanged(nameof(CanChangeAudioSelection));
        OnPropertyChanged(nameof(CanAdjustAudio));
        OnPropertyChanged(nameof(MuteAccessText));
        ToggleAudioMuteCommand.NotifyCanExecuteChanged();
    }

    private void SafeLogInformation(string message)
    {
        try { logger.Information(message); }
        catch (Exception) { }
    }

    private void SafeLogWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }

    private void NotifyFullscreenProperties()
    {
        OnPropertyChanged(nameof(IsFullscreen));
        OnPropertyChanged(nameof(IsFullscreenTransitioning));
        OnPropertyChanged(nameof(IsFullscreenPresentation));
        OnPropertyChanged(nameof(IsWindowedChromeVisible));
        OnPropertyChanged(nameof(CanToggleFullscreen));
        OnPropertyChanged(nameof(CanFullscreen));
        OnPropertyChanged(nameof(FullscreenAccessText));
        OnPropertyChanged(nameof(CanOpenInformationDialog));
        OnPropertyChanged(nameof(IsMainContentEnabled));
        ToggleFullscreenCommand.NotifyCanExecuteChanged();
        ShowSettingsInformationCommand.NotifyCanExecuteChanged();
        ShowHelpInformationCommand.NotifyCanExecuteChanged();
    }

    private void ApplySelectDeviceState()
    {
        StatusMessage = "Select a video capture device.";
        PreviewGlyph = "\uE895";
        PreviewTitle = "Select a capture device";
        PreviewDescription = "Choose a Windows video input below to inspect its supported formats.";
    }

    private void ApplyCurrentDeviceCancellation()
    {
        if (SessionState == CaptureSessionState.Enumerating) TryTransition(CaptureSessionState.Idle);
        StatusMessage = "No compatible video capture devices found.";
        PreviewGlyph = "\uE711";
        PreviewTitle = "No capture device detected";
        PreviewDescription = "Connect a compatible USB HDMI capture card and select Refresh.";
    }

    private void ApplyCurrentFormatCancellation()
    {
        if (SessionState == CaptureSessionState.Enumerating) TryTransition(CaptureSessionState.Idle);
        StatusMessage = "Format discovery was cancelled.";
        PreviewGlyph = "\uE71A";
        PreviewTitle = "Format discovery cancelled";
        PreviewDescription = "Select another capture device or select Refresh to try again.";
    }

    private void ApplyDiscoveryFailureMessage(DiscoveryFailure failure)
    {
        logger.Warning($"Video discovery failed during {failure.Operation} ({failure.HResultDisplay}).");
        PreviewGlyph = "\uE783";
        switch (failure.Category)
        {
            case DiscoveryFailureCategory.MissingMediaComponents:
                StatusMessage = "Required Windows media components are unavailable.";
                PreviewTitle = "Video discovery unavailable";
                PreviewDescription = "Required Windows media components are unavailable. Install the Media Feature Pack, restart Windows, and select Refresh.";
                break;
            case DiscoveryFailureCategory.NoUsableFormats:
                StatusMessage = "The selected device exposed no usable native video formats.";
                PreviewTitle = "No native formats available";
                PreviewDescription = "Select another capture device or refresh the device list.";
                break;
            case DiscoveryFailureCategory.AccessDenied:
                StatusMessage = "Windows camera access is disabled.";
                PreviewTitle = "Camera access required";
                PreviewDescription = "Enable camera access for desktop apps in Windows Privacy & security settings, then select Refresh.";
                break;
            default:
                StatusMessage = "Video discovery is unavailable. Select Refresh to try again.";
                PreviewTitle = "Video discovery unavailable";
                PreviewDescription = "Windows could not complete video device discovery. Select Refresh to try again.";
                break;
        }
    }

    private void ApplyPreviewFailureMessage(PreviewFailure failure)
    {
        logger.Warning($"Preview failed safely: {failure.Category}.");
        PreviewGlyph = "\uE783";
        StatusMessage = failure.Category switch
        {
            PreviewFailureCategory.AccessDenied => "Windows camera access must be enabled before preview can start.",
            PreviewFailureCategory.DeviceBusy => "The selected camera is in use by another application.",
            PreviewFailureCategory.DeviceUnavailable => "The selected video device is unavailable.",
            PreviewFailureCategory.SelectedFormatUnavailable => "The selected native format is no longer available.",
            PreviewFailureCategory.DecoderUnavailable => "Windows could not decode the selected native format.",
            PreviewFailureCategory.D3DInitializationFailure => "GPU preview could not be initialized.",
            PreviewFailureCategory.DeviceRemoved => "The graphics device was reset or removed.",
            PreviewFailureCategory.UnsupportedGpuBuffer or PreviewFailureCategory.UnsupportedPreviewFormat => "The selected format cannot use the required GPU preview path.",
            PreviewFailureCategory.PreviewStalled => "Video input stalled.",
            PreviewFailureCategory.StartupTimeout => "Preview startup timed out.",
            PreviewFailureCategory.ShutdownTimeout => "Preview shutdown exceeded the safety timeout.",
            _ => "Live preview stopped unexpectedly."
        };
        PreviewTitle = failure.Category == PreviewFailureCategory.PreviewStalled ? "Video input stalled" : "Preview unavailable";
        PreviewDescription = failure.Category switch
        {
            PreviewFailureCategory.AccessDenied => "Enable camera access for desktop apps in Windows Privacy & security settings, then try again.",
            PreviewFailureCategory.DeviceBusy => "Close other camera applications and select Start again.",
            PreviewFailureCategory.SelectedFormatUnavailable => "Refresh the device list and select an available native format.",
            PreviewFailureCategory.DecoderUnavailable or PreviewFailureCategory.UnsupportedGpuBuffer or PreviewFailureCategory.UnsupportedPreviewFormat => "Select another native video format and try again.",
            PreviewFailureCategory.StartupTimeout => "Verify the device connection and try Start again.",
            _ => "Stop the preview if necessary, verify the device connection, and try again."
        };
    }

    private void TryTransition(CaptureSessionState target)
    {
        if (stateMachine.CanTransitionTo(target)) stateMachine.TryTransitionTo(target);
    }

    private void OnStateChanged(object? sender, CaptureSessionState state)
    {
        _ = sender;
        _ = state;
        OnPropertyChanged(nameof(SessionState));
        OnPropertyChanged(nameof(SessionStateDisplay));
        NotifyCaptureProperties();
        UpdatePreviewSurfacePresentation();
    }

    private void UpdatePreviewSurfacePresentation()
    {
        if (previewSurface is null) return;

        var surfaceActive = !disposed && IsPreviewActive && !IsInformationDialogOpen;
        previewSurface.SetSurfaceActive(surfaceActive);

        var videoVisible =
            !disposed &&
            !IsInformationDialogOpen &&
            SessionState == CaptureSessionState.Previewing &&
            previewSurface.IsAvailable &&
            previewSurface.IsPresentable;
        previewSurface.SetVideoVisible(videoVisible);
    }

    private static string GetSessionStateDisplay(CaptureSessionState state) => state switch
    {
        CaptureSessionState.Idle => "Idle",
        CaptureSessionState.Enumerating => "Enumerating",
        CaptureSessionState.DeviceReady => "Device ready",
        CaptureSessionState.Starting => "Starting",
        CaptureSessionState.Previewing => "Previewing",
        CaptureSessionState.Recording => "Recording",
        CaptureSessionState.Reconnecting => "Reconnecting",
        CaptureSessionState.Stopping => "Stopping",
        _ => "Error"
    };
}

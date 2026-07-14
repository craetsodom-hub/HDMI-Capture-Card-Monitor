using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationLogger logger;
    private readonly CaptureSessionStateMachine stateMachine;
    private readonly ICaptureDeviceDiscoveryService discoveryService;
    private CancellationTokenSource? deviceScanCancellation;
    private CancellationTokenSource? formatScanCancellation;
    private int deviceScanGeneration;
    private int formatScanGeneration;
    private Task formatDiscoveryCompletion = Task.CompletedTask;
    private readonly bool captureActionsAvailable;
    private bool disposed;

    public ObservableCollection<CaptureDevice> Devices { get; } = [];
    public ObservableCollection<NativeVideoCapability> Formats { get; } = [];
    [ObservableProperty] private string statusMessage = "Scanning for video devices…";
    [ObservableProperty] private string previewTitle = "Scanning for video devices…";
    [ObservableProperty] private string previewDescription = "Please wait while Windows checks available video inputs.";
    [ObservableProperty] private bool isDeviceScanRunning;
    [ObservableProperty] private bool isFormatScanRunning;
    [ObservableProperty] private CaptureDevice? selectedDevice;
    [ObservableProperty] private NativeVideoCapability? selectedFormat;

    public CaptureSessionState SessionState => stateMachine.CurrentState;
    public string SessionStateDisplay => GetSessionStateDisplay(SessionState);
    public bool HasDevices => Devices.Count > 0 && !IsDeviceScanRunning;
    public bool HasFormats => Formats.Count > 0 && !IsDeviceScanRunning;
    public bool CanRefreshDevices => !IsDeviceScanRunning;
    public string DevicePlaceholder => IsDeviceScanRunning ? "No device available" : HasDevices ? "Select a capture device" : "No device available";
    public string FormatPlaceholder => SelectedDevice is null ? "Select a device first" : HasFormats ? "Select a native format" : "No native formats available";
    public bool CanStartCapture => captureActionsAvailable;
    public bool CanFullscreen => captureActionsAvailable;
    public bool CanTakeSnapshot => captureActionsAvailable;
    public bool CanRecord => captureActionsAvailable;
    internal Task FormatDiscoveryCompletion => formatDiscoveryCompletion;

    public MainWindowViewModel(IApplicationLogger logger, string? startupNotice = null, CaptureSessionStateMachine? stateMachine = null, ICaptureDeviceDiscoveryService? discoveryService = null)
    {
        this.logger = logger;
        this.discoveryService = discoveryService ?? new UnavailableCaptureDeviceDiscoveryService("Video device discovery is unavailable.");
        this.stateMachine = stateMachine ?? new CaptureSessionStateMachine();
        captureActionsAvailable = false;
        this.stateMachine.StateChanged += OnStateChanged;
        if (!string.IsNullOrWhiteSpace(startupNotice)) StatusMessage = startupNotice;
    }

    public void StartInitialDiscovery() => _ = RefreshDevicesAsync();
    internal Task RefreshDevicesForTestingAsync() => RefreshDevicesAsync();

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        var generation = Interlocked.Increment(ref deviceScanGeneration);
        var previous = Interlocked.Exchange(ref deviceScanCancellation, new CancellationTokenSource());
        previous?.Cancel(); previous?.Dispose();
        var localCancellation = deviceScanCancellation!;
        var token = localCancellation.Token;
        CancelFormats();
        Devices.Clear(); Formats.Clear(); SelectedDevice = null; SelectedFormat = null;
        NotifyDeviceProperties();
        IsDeviceScanRunning = true; RefreshDevicesCommand.NotifyCanExecuteChanged();
        TryTransition(CaptureSessionState.Enumerating);
        StatusMessage = "Scanning for video devices…"; PreviewTitle = StatusMessage; PreviewDescription = "Please wait while Windows checks available video inputs.";
        try
        {
            var result = await discoveryService.EnumerateVideoDevicesAsync(token);
            if (!IsCurrentDeviceRequest(generation) || disposed) return;
            if (result.IsCancelled) return;
            if (!result.IsSuccess) { ApplyFailureMessage(result.Failure!); TryTransition(CaptureSessionState.Faulted); return; }
            foreach (var device in result.Value ?? []) Devices.Add(device);
            NotifyDeviceProperties();
            if (Devices.Count == 0) { StatusMessage = "No compatible video capture devices found."; PreviewTitle = "No capture device detected"; PreviewDescription = "Connect a compatible USB HDMI capture card and select Refresh."; }
            else { StatusMessage = "Select a video capture device."; PreviewTitle = "Select a capture device"; PreviewDescription = "Choose a Windows video input below to inspect its supported formats."; }
            TryTransition(CaptureSessionState.Idle);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrentDeviceRequest(generation) || disposed) return;
            logger.LogError("Device enumeration failed.", exception);
            ApplyFailureMessage(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, DiscoveryFailureCategory.Unknown, null, "Device enumeration failed.", exception)); TryTransition(CaptureSessionState.Faulted);
        }
        finally { if (IsCurrentDeviceRequest(generation)) { IsDeviceScanRunning = false; NotifyDeviceProperties(); RefreshDevicesCommand.NotifyCanExecuteChanged(); } }
    }

    partial void OnSelectedDeviceChanged(CaptureDevice? value) { formatDiscoveryCompletion = LoadFormatsAsync(value); NotifyDeviceProperties(); }

    private async Task LoadFormatsAsync(CaptureDevice? device)
    {
        var generation = Interlocked.Increment(ref formatScanGeneration);
        CancelFormats(); Formats.Clear(); SelectedFormat = null; NotifyDeviceProperties();
        if (device is null) { if (SessionState == CaptureSessionState.DeviceReady) TryTransition(CaptureSessionState.Idle); return; }
        formatScanCancellation = new CancellationTokenSource(); var token = formatScanCancellation.Token;
        IsFormatScanRunning = true; TryTransition(CaptureSessionState.Enumerating);
        StatusMessage = "Reading supported formats…"; PreviewTitle = StatusMessage; PreviewDescription = "The selected device is being inspected.";
        try
        {
            var result = await discoveryService.GetNativeVideoCapabilitiesAsync(device, token);
            if (!IsCurrentFormatRequest(generation, device) || disposed) return;
            if (result.IsCancelled) return;
            if (!result.IsSuccess) { ApplyFailureMessage(result.Failure!); TryTransition(CaptureSessionState.Faulted); return; }
            foreach (var format in result.Value ?? []) Formats.Add(format);
            NotifyDeviceProperties();
            if (Formats.Count == 0) { StatusMessage = "The selected device exposed no usable native video formats."; PreviewTitle = "No native formats available"; PreviewDescription = "Select another capture device or refresh the device list."; }
            else { StatusMessage = $"Device ready · {Formats.Count} formats available"; PreviewTitle = "Capture device ready"; PreviewDescription = "Select a native video format below."; }
            TryTransition(CaptureSessionState.DeviceReady);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrentFormatRequest(generation, device) || disposed) return;
            logger.LogError("Native format discovery failed.", exception);
            ApplyFailureMessage(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, DiscoveryFailureCategory.Unknown, null, "Native format discovery failed.", exception)); TryTransition(CaptureSessionState.Faulted);
        }
        finally { if (IsCurrentFormatRequest(generation, device)) IsFormatScanRunning = false; }
    }

    [RelayCommand] private void ShowSettingsInformation() => StatusMessage = "Settings are not available yet.";
    [RelayCommand] private void ShowHelpInformation() => StatusMessage = "Help content is not available yet.";
    public void Dispose() { if (disposed) return; disposed = true; Interlocked.Increment(ref deviceScanGeneration); Interlocked.Increment(ref formatScanGeneration); CancelFormats(); deviceScanCancellation?.Cancel(); deviceScanCancellation?.Dispose(); stateMachine.StateChanged -= OnStateChanged; }
    private void CancelFormats() { var previous = Interlocked.Exchange(ref formatScanCancellation, null); previous?.Cancel(); previous?.Dispose(); }
    private bool IsCurrentDeviceRequest(int generation) => generation == Volatile.Read(ref deviceScanGeneration);
    private bool IsCurrentFormatRequest(int generation, CaptureDevice device) => generation == Volatile.Read(ref formatScanGeneration) && ReferenceEquals(device, SelectedDevice);
    private void NotifyDeviceProperties() { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(HasFormats)); OnPropertyChanged(nameof(DevicePlaceholder)); OnPropertyChanged(nameof(FormatPlaceholder)); }
    private void ApplyFailureMessage(DiscoveryFailure failure)
    {
        logger.Warning($"Video discovery failed during {failure.Operation} ({failure.HResultDisplay}).");
        if (failure.Category == DiscoveryFailureCategory.MissingMediaComponents) { StatusMessage = "Required Windows media components are unavailable."; PreviewTitle = "Video discovery unavailable"; PreviewDescription = "Required Windows media components are unavailable. Install the Media Feature Pack, restart Windows, and select Refresh."; }
        else { StatusMessage = "Video discovery is unavailable. Select Refresh to try again."; PreviewTitle = "Video discovery unavailable"; PreviewDescription = "Windows could not complete video device discovery. Select Refresh to try again."; }
    }
    private void TryTransition(CaptureSessionState target) { if (stateMachine.CanTransitionTo(target)) stateMachine.TryTransitionTo(target); }
    private void OnStateChanged(object? sender, CaptureSessionState _) { OnPropertyChanged(nameof(SessionState)); OnPropertyChanged(nameof(SessionStateDisplay)); }
    private static string GetSessionStateDisplay(CaptureSessionState state) => state switch { CaptureSessionState.Idle => "Idle", CaptureSessionState.Enumerating => "Enumerating", CaptureSessionState.DeviceReady => "Device ready", CaptureSessionState.Starting => "Starting", CaptureSessionState.Previewing => "Previewing", CaptureSessionState.Recording => "Recording", CaptureSessionState.Reconnecting => "Reconnecting", CaptureSessionState.Stopping => "Stopping", _ => "Error" };
}

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
    private bool disposed;

    public ObservableCollection<CaptureDevice> Devices { get; } = [];
    public ObservableCollection<NativeVideoCapability> Formats { get; } = [];

    [ObservableProperty] private string statusMessage = "Scanning for video devices…";
    [ObservableProperty] private bool isDeviceScanRunning;
    [ObservableProperty] private bool isFormatScanRunning;
    [ObservableProperty] private CaptureDevice? selectedDevice;
    [ObservableProperty] private NativeVideoCapability? selectedFormat;

    public CaptureSessionState SessionState => stateMachine.CurrentState;
    public string SessionStateDisplay => GetSessionStateDisplay(SessionState);
    public bool HasDevices => Devices.Count > 0;
    public bool HasFormats => Formats.Count > 0;
    public bool CanRefreshDevices => !IsDeviceScanRunning;

    public MainWindowViewModel(
        IApplicationLogger logger,
        string? startupNotice = null,
        CaptureSessionStateMachine? stateMachine = null,
        ICaptureDeviceDiscoveryService? discoveryService = null)
    {
        this.logger = logger;
        this.discoveryService = discoveryService ?? new UnavailableCaptureDeviceDiscoveryService("Video device discovery is unavailable.");
        this.stateMachine = stateMachine ?? new CaptureSessionStateMachine();
        this.stateMachine.StateChanged += OnStateChanged;
        if (!string.IsNullOrWhiteSpace(startupNotice)) StatusMessage = startupNotice;
    }

    public void StartInitialDiscovery() => _ = RefreshDevicesAsync();

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        var generation = Interlocked.Increment(ref deviceScanGeneration);
        deviceScanCancellation?.Cancel();
        deviceScanCancellation?.Dispose();
        deviceScanCancellation = new CancellationTokenSource();
        formatScanCancellation?.Cancel();
        Formats.Clear();
        SelectedFormat = null;
        IsDeviceScanRunning = true;
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        TransitionToEnumerating();
        StatusMessage = "Scanning for video devices…";

        try
        {
            var result = await discoveryService.EnumerateVideoDevicesAsync(deviceScanCancellation.Token);
            if (generation != deviceScanGeneration) return;
            if (result.IsCancelled) return;
            if (!result.IsSuccess)
            {
                logger.Warning($"Video device discovery failed during {result.Failure!.Operation} ({result.Failure.HResultDisplay}).");
                StatusMessage = "Video device discovery is unavailable. Select Refresh to try again.";
                TryTransition(CaptureSessionState.Faulted);
                return;
            }

            Devices.Clear();
            foreach (var device in result.Value ?? []) Devices.Add(device);
            SelectedDevice = null;
            OnPropertyChanged(nameof(HasDevices));
            if (Devices.Count == 0)
            {
                StatusMessage = "No compatible video capture devices found.";
                TryTransition(CaptureSessionState.Idle);
            }
            else
            {
                StatusMessage = "Select a video capture device.";
                TryTransition(CaptureSessionState.Idle);
            }
        }
        catch (OperationCanceledException) when (deviceScanCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError("Device enumeration failed.", exception);
            StatusMessage = "Video device discovery failed. Select Refresh to try again.";
            TryTransition(CaptureSessionState.Faulted);
        }
        finally
        {
            if (generation == deviceScanGeneration)
            {
                IsDeviceScanRunning = false;
                RefreshDevicesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    partial void OnSelectedDeviceChanged(CaptureDevice? value)
    {
        OnPropertyChanged(nameof(HasFormats));
        _ = LoadFormatsAsync(value);
    }

    private async Task LoadFormatsAsync(CaptureDevice? device)
    {
        var generation = Interlocked.Increment(ref formatScanGeneration);
        formatScanCancellation?.Cancel();
        formatScanCancellation?.Dispose();
        Formats.Clear();
        SelectedFormat = null;
        OnPropertyChanged(nameof(HasFormats));
        if (device is null)
        {
            if (SessionState == CaptureSessionState.DeviceReady) TryTransition(CaptureSessionState.Idle);
            return;
        }

        formatScanCancellation = new CancellationTokenSource();
        IsFormatScanRunning = true;
        TransitionToEnumerating();
        StatusMessage = "Reading supported formats…";
        try
        {
            var result = await discoveryService.GetNativeVideoCapabilitiesAsync(device, formatScanCancellation.Token);
            if (generation != formatScanGeneration || !ReferenceEquals(device, SelectedDevice)) return;
            if (result.IsCancelled) return;
            if (!result.IsSuccess)
            {
                StatusMessage = "The selected video device is unavailable. Select Refresh or another device.";
                TryTransition(CaptureSessionState.Faulted);
                return;
            }

            foreach (var format in result.Value ?? []) Formats.Add(format);
            OnPropertyChanged(nameof(HasFormats));
            StatusMessage = Formats.Count == 0 ? "The selected device exposed no usable native video formats." : $"Device ready · {Formats.Count} formats available";
            TryTransition(CaptureSessionState.DeviceReady);
        }
        catch (OperationCanceledException) when (formatScanCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError("Native format discovery failed.", exception);
            StatusMessage = "The selected video device is unavailable. Select Refresh or another device.";
            TryTransition(CaptureSessionState.Faulted);
        }
        finally
        {
            if (generation == formatScanGeneration) IsFormatScanRunning = false;
        }
    }

    [RelayCommand] private void ShowSettingsInformation() => StatusMessage = "Settings are not available yet.";
    [RelayCommand] private void ShowHelpInformation() => StatusMessage = "Help content is not available yet.";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        deviceScanCancellation?.Cancel();
        formatScanCancellation?.Cancel();
        deviceScanCancellation?.Dispose();
        formatScanCancellation?.Dispose();
    }

    private void TransitionToEnumerating() => TryTransition(CaptureSessionState.Enumerating);
    private void TryTransition(CaptureSessionState target) { if (stateMachine.CanTransitionTo(target)) stateMachine.TryTransitionTo(target); }
    private void OnStateChanged(object? sender, CaptureSessionState _) { OnPropertyChanged(nameof(SessionState)); OnPropertyChanged(nameof(SessionStateDisplay)); }
    private static string GetSessionStateDisplay(CaptureSessionState state) => state switch
    {
        CaptureSessionState.Idle => "Idle", CaptureSessionState.Enumerating => "Enumerating", CaptureSessionState.DeviceReady => "Device ready", CaptureSessionState.Starting => "Starting", CaptureSessionState.Previewing => "Previewing", CaptureSessionState.Recording => "Recording", CaptureSessionState.Reconnecting => "Reconnecting", CaptureSessionState.Stopping => "Stopping", _ => "Error"
    };
}

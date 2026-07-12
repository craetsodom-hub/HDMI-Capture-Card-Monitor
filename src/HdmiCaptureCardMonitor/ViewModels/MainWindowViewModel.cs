using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationLogger logger;
    private readonly CaptureSessionStateMachine stateMachine;

    [ObservableProperty]
    private string statusMessage = "No capture device selected";

    public CaptureSessionState SessionState => stateMachine.CurrentState;

    public string SessionStateDisplay => GetSessionStateDisplay(SessionState);

    public MainWindowViewModel(
        IApplicationLogger logger,
        string? startupNotice = null,
        CaptureSessionStateMachine? stateMachine = null)
    {
        this.logger = logger;
        this.stateMachine = stateMachine ?? new CaptureSessionStateMachine();
        this.stateMachine.StateChanged += OnStateChanged;

        logger.Information("Application shell started without a selected capture device.");
        if (!string.IsNullOrWhiteSpace(startupNotice))
        {
            StatusMessage = startupNotice;
        }
    }

    [RelayCommand]
    private void ShowSettingsInformation()
    {
        logger.Information("Settings entry point selected.");
        StatusMessage = "Settings are not available yet.";
    }

    [RelayCommand]
    private void ShowHelpInformation()
    {
        logger.Information("Help entry point selected.");
        StatusMessage = "Help content is not available yet.";
    }

    private void OnStateChanged(object? sender, CaptureSessionState _)
    {
        OnPropertyChanged(nameof(SessionState));
        OnPropertyChanged(nameof(SessionStateDisplay));
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
        CaptureSessionState.Faulted => "Error",
        _ => "Error"
    };
}

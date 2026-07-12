using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationLogger logger;

    [ObservableProperty] private string statusMessage = "No capture device selected";
    [ObservableProperty] private CaptureSessionState sessionState = CaptureSessionState.Idle;

    public MainWindowViewModel(IApplicationLogger logger)
    {
        this.logger = logger;
        logger.Information("Application shell started without a selected capture device.");
    }

    [RelayCommand]
    private void ShowSettingsInformation()
    {
        logger.Information("Settings entry point selected.");
        StatusMessage = "Settings will be available in a later phase.";
    }

    [RelayCommand]
    private void ShowHelpInformation()
    {
        logger.Information("Help entry point selected.");
        StatusMessage = "Help is not available yet. See README.md for Phase 0 scope.";
    }
}

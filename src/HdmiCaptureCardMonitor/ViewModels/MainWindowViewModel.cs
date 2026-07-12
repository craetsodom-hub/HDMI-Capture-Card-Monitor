using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationLogger logger;

    [ObservableProperty] private string statusMessage = "No capture device selected";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionStateDisplay))]
    private CaptureSessionState sessionState = CaptureSessionState.Idle;

    public string SessionStateDisplay => $"Phase 0 · {SessionState}";

    public MainWindowViewModel(IApplicationLogger logger, string? startupNotice = null)
    {
        this.logger = logger;
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
        StatusMessage = "Settings will be available in a later phase.";
    }

    [RelayCommand]
    private void ShowHelpInformation()
    {
        logger.Information("Help entry point selected.");
        StatusMessage = "Help is not available yet. See README.md for Phase 0 scope.";
    }
}

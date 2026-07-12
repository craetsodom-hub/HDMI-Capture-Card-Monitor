using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void LifecycleDisplayTracksTheCurrentSessionState()
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);

        viewModel.SessionState = CaptureSessionState.Previewing;

        Assert.Equal("Phase 0 · Previewing", viewModel.SessionStateDisplay);
    }

    [Fact]
    public void SettingsAndHelpCommandsProvideNonFatalPhaseZeroGuidance()
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);

        viewModel.ShowSettingsInformationCommand.Execute(null);
        Assert.Equal("Settings will be available in a later phase.", viewModel.StatusMessage);

        viewModel.ShowHelpInformationCommand.Execute(null);
        Assert.Equal("Help is not available yet. See README.md for Phase 0 scope.", viewModel.StatusMessage);
    }
}

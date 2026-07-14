using System.ComponentModel;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class MainWindowViewModelTests
{
    public static IEnumerable<object[]> CustomerFacingLabels =>
        new[]
        {
            new object[] { CaptureSessionState.Idle, "Idle" },
            new object[] { CaptureSessionState.Enumerating, "Enumerating" },
            new object[] { CaptureSessionState.DeviceReady, "Device ready" },
            new object[] { CaptureSessionState.Starting, "Starting" },
            new object[] { CaptureSessionState.Previewing, "Previewing" },
            new object[] { CaptureSessionState.Recording, "Recording" },
            new object[] { CaptureSessionState.Reconnecting, "Reconnecting" },
            new object[] { CaptureSessionState.Stopping, "Stopping" },
            new object[] { CaptureSessionState.Faulted, "Error" }
        };

    [Theory]
    [MemberData(nameof(CustomerFacingLabels))]
    public void LifecycleDisplayFollowsTheInjectedStateMachine(CaptureSessionState state, string expectedLabel)
    {
        var stateMachine = MoveTo(state);
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, stateMachine: stateMachine);

        Assert.Equal(state, viewModel.SessionState);
        Assert.Equal(expectedLabel, viewModel.SessionStateDisplay);
        Assert.DoesNotContain("Phase", viewModel.SessionStateDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedStateMachineTransitionsNotifyTheViewModelExactlyOnce()
    {
        var stateMachine = new CaptureSessionStateMachine();
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, stateMachine: stateMachine);
        var sessionStateNotifications = 0;
        var displayNotifications = 0;
        viewModel.PropertyChanged += (_, eventArgs) => CountLifecycleNotification(eventArgs, ref sessionStateNotifications, ref displayNotifications);

        Assert.True(stateMachine.TryTransitionTo(CaptureSessionState.Enumerating));

        Assert.Equal(1, sessionStateNotifications);
        Assert.Equal(1, displayNotifications);
        Assert.Equal(CaptureSessionState.Enumerating, viewModel.SessionState);
        Assert.Equal("Enumerating", viewModel.SessionStateDisplay);
    }

    [Fact]
    public void RejectedStateMachineTransitionsDoNotNotifyTheViewModel()
    {
        var stateMachine = new CaptureSessionStateMachine();
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, stateMachine: stateMachine);
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        Assert.False(stateMachine.TryTransitionTo(CaptureSessionState.Recording));

        Assert.Equal(0, notifications);
        Assert.Equal(CaptureSessionState.Idle, viewModel.SessionState);
    }

    [Fact]
    public void SessionStateIsNotExternallyWritable()
    {
        var property = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SessionState));

        Assert.NotNull(property);
        Assert.False(property.CanWrite);
    }

    [Fact]
    public void StartupLoggingFailureNoticeIsDisplayed()
    {
        const string startupNotice = "Local logging is unavailable. The application will continue without file logs.";
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, startupNotice);

        Assert.Equal(startupNotice, viewModel.StatusMessage);
    }

    [Fact]
    public void SettingsAndHelpCommandsProvideHonestNonPhaseGuidance()
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);

        viewModel.ShowSettingsInformationCommand.Execute(null);
        Assert.Equal("Settings are not available yet.", viewModel.StatusMessage);
        Assert.DoesNotContain("Phase", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsInformationDialogOpen);
        Assert.Contains("Settings", viewModel.InformationDialogTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phase", viewModel.InformationDialogDescription, StringComparison.OrdinalIgnoreCase);

        viewModel.CloseInformationDialogCommand.Execute(null);
        Assert.False(viewModel.IsInformationDialogOpen);

        viewModel.ShowHelpInformationCommand.Execute(null);
        Assert.Equal("Help content is available in the open information panel.", viewModel.StatusMessage);
        Assert.DoesNotContain("Phase", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsInformationDialogOpen);
        Assert.Contains("camera access", viewModel.InformationDialogDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USB HDMI", viewModel.InformationDialogDetails, StringComparison.OrdinalIgnoreCase);
    }

    private static void CountLifecycleNotification(
        PropertyChangedEventArgs eventArgs,
        ref int sessionStateNotifications,
        ref int displayNotifications)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SessionState))
        {
            sessionStateNotifications++;
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.SessionStateDisplay))
        {
            displayNotifications++;
        }
    }

    private static CaptureSessionStateMachine MoveTo(CaptureSessionState target)
    {
        var machine = new CaptureSessionStateMachine();
        var paths = new Dictionary<CaptureSessionState, CaptureSessionState[]>
        {
            [CaptureSessionState.Idle] = [],
            [CaptureSessionState.Enumerating] = [CaptureSessionState.Enumerating],
            [CaptureSessionState.DeviceReady] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady],
            [CaptureSessionState.Starting] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting],
            [CaptureSessionState.Previewing] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing],
            [CaptureSessionState.Recording] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing, CaptureSessionState.Recording],
            [CaptureSessionState.Reconnecting] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing, CaptureSessionState.Reconnecting],
            [CaptureSessionState.Stopping] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing, CaptureSessionState.Stopping],
            [CaptureSessionState.Faulted] = [CaptureSessionState.Enumerating, CaptureSessionState.Faulted]
        };

        foreach (var next in paths[target])
        {
            Assert.True(machine.TryTransitionTo(next));
        }

        return machine;
    }
}

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

    public static IEnumerable<object[]> LifecycleHints =>
        new[]
        {
            new object[] { CaptureSessionState.Idle, "Select a capture device to continue." },
            new object[] { CaptureSessionState.Enumerating, "Scanning for available video devices." },
            new object[] { CaptureSessionState.DeviceReady, "Select a native format to enable preview." },
            new object[] { CaptureSessionState.Starting, "Preparing the GPU preview." },
            new object[] { CaptureSessionState.Previewing, "Live preview is active." },
            new object[] { CaptureSessionState.Stopping, "Releasing the video device safely." },
            new object[] { CaptureSessionState.Faulted, "Review the message above, correct the issue, and try again." }
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
        const string lifecycleStatus = "No compatible video capture devices found.";
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, lifecycleStatus);

        viewModel.ShowSettingsInformationCommand.Execute(null);
        Assert.Equal(lifecycleStatus, viewModel.StatusMessage);
        Assert.True(viewModel.IsInformationDialogOpen);
        Assert.False(viewModel.IsMainContentEnabled);
        Assert.False(viewModel.ShowSettingsInformationCommand.CanExecute(null));
        Assert.False(viewModel.ShowHelpInformationCommand.CanExecute(null));
        Assert.True(viewModel.CloseInformationDialogCommand.CanExecute(null));
        Assert.False(viewModel.CanRefreshDevices);
        Assert.Contains("Settings", viewModel.InformationDialogTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phase", viewModel.InformationDialogDescription, StringComparison.OrdinalIgnoreCase);

        viewModel.CloseInformationDialogCommand.Execute(null);
        Assert.False(viewModel.IsInformationDialogOpen);
        Assert.True(viewModel.IsMainContentEnabled);
        Assert.True(viewModel.ShowSettingsInformationCommand.CanExecute(null));
        Assert.False(viewModel.CloseInformationDialogCommand.CanExecute(null));

        viewModel.ShowHelpInformationCommand.Execute(null);
        Assert.Equal(lifecycleStatus, viewModel.StatusMessage);
        Assert.True(viewModel.IsInformationDialogOpen);
        Assert.Contains("camera access", viewModel.InformationDialogDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USB HDMI", viewModel.InformationDialogDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(LifecycleHints))]
    public void PreviewControlHintFollowsLifecycleState(CaptureSessionState state, string expectedHint)
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance, stateMachine: MoveTo(state));

        Assert.Equal(expectedHint, viewModel.PreviewControlHint);
        Assert.DoesNotContain("Phase", viewModel.PreviewControlHint, StringComparison.OrdinalIgnoreCase);
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

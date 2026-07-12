using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class CaptureSessionStateMachineTests
{
    [Fact]
    public void StartsIdle() => Assert.Equal(CaptureSessionState.Idle, new CaptureSessionStateMachine().CurrentState);

    [Fact]
    public void AllowsExpectedStartupPath()
    {
        var machine = new CaptureSessionStateMachine();
        Assert.True(machine.TryTransitionTo(CaptureSessionState.Enumerating));
        Assert.True(machine.TryTransitionTo(CaptureSessionState.DeviceReady));
        Assert.True(machine.TryTransitionTo(CaptureSessionState.Starting));
        Assert.True(machine.TryTransitionTo(CaptureSessionState.Previewing));
        Assert.Equal(CaptureSessionState.Previewing, machine.CurrentState);
    }

    [Fact]
    public void RejectsInvalidTransitionWithoutChangingState()
    {
        var machine = new CaptureSessionStateMachine();
        Assert.False(machine.TryTransitionTo(CaptureSessionState.Recording));
        Assert.Equal(CaptureSessionState.Idle, machine.CurrentState);
    }

    [Theory]
    [InlineData(CaptureSessionState.Previewing, CaptureSessionState.Recording)]
    [InlineData(CaptureSessionState.Previewing, CaptureSessionState.Reconnecting)]
    [InlineData(CaptureSessionState.Recording, CaptureSessionState.Stopping)]
    [InlineData(CaptureSessionState.Reconnecting, CaptureSessionState.Starting)]
    public void AllowsLifecycleTransitionsFromReachableState(CaptureSessionState source, CaptureSessionState target)
    {
        var machine = MoveTo(source);
        Assert.True(machine.TryTransitionTo(target));
        Assert.Equal(target, machine.CurrentState);
    }

    [Fact]
    public void RaisesStateChangedForAcceptedTransition()
    {
        var machine = new CaptureSessionStateMachine();
        CaptureSessionState? observed = null;
        machine.StateChanged += (_, state) => observed = state;
        machine.TryTransitionTo(CaptureSessionState.Enumerating);
        Assert.Equal(CaptureSessionState.Enumerating, observed);
    }

    private static CaptureSessionStateMachine MoveTo(CaptureSessionState state)
    {
        var machine = new CaptureSessionStateMachine();
        var paths = new Dictionary<CaptureSessionState, CaptureSessionState[]>
        {
            [CaptureSessionState.Previewing] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing],
            [CaptureSessionState.Recording] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing, CaptureSessionState.Recording],
            [CaptureSessionState.Reconnecting] = [CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Previewing, CaptureSessionState.Reconnecting]
        };
        foreach (var next in paths[state]) Assert.True(machine.TryTransitionTo(next));
        return machine;
    }
}

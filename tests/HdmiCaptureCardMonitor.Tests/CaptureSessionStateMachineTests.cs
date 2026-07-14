using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class CaptureSessionStateMachineTests
{
    private static readonly Dictionary<CaptureSessionState, HashSet<CaptureSessionState>> ExpectedTransitions =
        new()
        {
            [CaptureSessionState.Idle] = Set(CaptureSessionState.Enumerating),
            [CaptureSessionState.Enumerating] = Set(CaptureSessionState.Idle, CaptureSessionState.DeviceReady, CaptureSessionState.Faulted),
            [CaptureSessionState.DeviceReady] = Set(CaptureSessionState.Idle, CaptureSessionState.Enumerating, CaptureSessionState.Starting),
            [CaptureSessionState.Starting] = Set(CaptureSessionState.DeviceReady, CaptureSessionState.Previewing, CaptureSessionState.Stopping, CaptureSessionState.Faulted),
            [CaptureSessionState.Previewing] = Set(CaptureSessionState.Recording, CaptureSessionState.Reconnecting, CaptureSessionState.Stopping, CaptureSessionState.Faulted),
            [CaptureSessionState.Recording] = Set(CaptureSessionState.Previewing, CaptureSessionState.Reconnecting, CaptureSessionState.Stopping, CaptureSessionState.Faulted),
            [CaptureSessionState.Reconnecting] = Set(CaptureSessionState.DeviceReady, CaptureSessionState.Starting, CaptureSessionState.Stopping, CaptureSessionState.Faulted),
            [CaptureSessionState.Stopping] = Set(CaptureSessionState.Idle, CaptureSessionState.DeviceReady, CaptureSessionState.Faulted),
            [CaptureSessionState.Faulted] = Set(CaptureSessionState.Idle, CaptureSessionState.Enumerating, CaptureSessionState.DeviceReady)
        };

    public static IEnumerable<object[]> EveryStatePair =>
        from source in Enum.GetValues<CaptureSessionState>()
        from target in Enum.GetValues<CaptureSessionState>()
        select new object[] { source, target, ExpectedTransitions[source].Contains(target) };

    [Fact]
    public void StartsIdle() => Assert.Equal(CaptureSessionState.Idle, new CaptureSessionStateMachine().CurrentState);

    [Theory]
    [MemberData(nameof(EveryStatePair))]
    public void HandlesEveryStatePairAccordingToTheExpectedTransitionTable(
        CaptureSessionState source,
        CaptureSessionState target,
        bool isAllowed)
    {
        var machine = MoveTo(source);
        var events = 0;
        machine.StateChanged += (_, _) => events++;

        Assert.Equal(isAllowed, machine.CanTransitionTo(target));
        var changed = machine.TryTransitionTo(target);

        Assert.Equal(isAllowed, changed);
        Assert.Equal(isAllowed ? target : source, machine.CurrentState);
        Assert.Equal(isAllowed ? 1 : 0, events);
    }

    [Fact]
    public void SupportsTheNormalStartupPath()
    {
        var machine = new CaptureSessionStateMachine();

        Transition(machine, CaptureSessionState.Enumerating);
        Transition(machine, CaptureSessionState.DeviceReady);
        Transition(machine, CaptureSessionState.Starting);
        Transition(machine, CaptureSessionState.Previewing);
    }

    [Fact]
    public void SupportsPreviewStartAndStop()
    {
        var machine = MoveTo(CaptureSessionState.Previewing);

        Transition(machine, CaptureSessionState.Stopping);
        Transition(machine, CaptureSessionState.Idle);
    }

    [Fact]
    public void StopsRecordingAndReturnsToTheLivePreview()
    {
        var machine = MoveTo(CaptureSessionState.Recording);

        Transition(machine, CaptureSessionState.Previewing);
    }

    [Fact]
    public void SupportsFaultRecovery()
    {
        var machine = MoveTo(CaptureSessionState.Previewing);

        Transition(machine, CaptureSessionState.Faulted);
        Transition(machine, CaptureSessionState.Enumerating);
        Transition(machine, CaptureSessionState.DeviceReady);
    }

    [Fact]
    public void SupportsPreviewRetryAfterCleanupWithSelectionsStillValid()
    {
        var machine = MoveTo(CaptureSessionState.Previewing);
        Transition(machine, CaptureSessionState.Faulted);
        Transition(machine, CaptureSessionState.DeviceReady);
        Transition(machine, CaptureSessionState.Starting);
    }

    [Fact]
    public void SupportsReconnectBehavior()
    {
        var machine = MoveTo(CaptureSessionState.Previewing);

        Transition(machine, CaptureSessionState.Reconnecting);
        Transition(machine, CaptureSessionState.Starting);
        Transition(machine, CaptureSessionState.Previewing);
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
            Transition(machine, next);
        }

        return machine;
    }

    private static HashSet<CaptureSessionState> Set(params CaptureSessionState[] states) => states.ToHashSet();

    private static void Transition(CaptureSessionStateMachine machine, CaptureSessionState next)
    {
        Assert.True(machine.TryTransitionTo(next));
        Assert.Equal(next, machine.CurrentState);
    }
}

namespace HdmiCaptureCardMonitor.Models;

/// <summary>Guards capture lifecycle transitions independently of any capture backend.</summary>
public sealed class CaptureSessionStateMachine
{
    private readonly object syncRoot = new();
    private CaptureSessionState currentState = CaptureSessionState.Idle;

    public CaptureSessionState CurrentState { get { lock (syncRoot) { return currentState; } } }
    public event EventHandler<CaptureSessionState>? StateChanged;

    public bool CanTransitionTo(CaptureSessionState nextState)
    {
        lock (syncRoot) { return IsAllowed(currentState, nextState); }
    }

    public bool TryTransitionTo(CaptureSessionState nextState)
    {
        EventHandler<CaptureSessionState>? handler;
        lock (syncRoot)
        {
            if (!IsAllowed(currentState, nextState)) return false;
            currentState = nextState;
            handler = StateChanged;
        }
        handler?.Invoke(this, nextState);
        return true;
    }

    private static bool IsAllowed(CaptureSessionState current, CaptureSessionState next) => current switch
    {
        CaptureSessionState.Idle => next == CaptureSessionState.Enumerating,
        CaptureSessionState.Enumerating => next is CaptureSessionState.Idle or CaptureSessionState.DeviceReady or CaptureSessionState.Faulted,
        CaptureSessionState.DeviceReady => next is CaptureSessionState.Idle or CaptureSessionState.Enumerating or CaptureSessionState.Starting,
        CaptureSessionState.Starting => next is CaptureSessionState.DeviceReady or CaptureSessionState.Previewing or CaptureSessionState.Stopping or CaptureSessionState.Faulted,
        CaptureSessionState.Previewing => next is CaptureSessionState.Recording or CaptureSessionState.Reconnecting or CaptureSessionState.Stopping or CaptureSessionState.Faulted,
        CaptureSessionState.Recording => next is CaptureSessionState.Previewing or CaptureSessionState.Reconnecting or CaptureSessionState.Stopping or CaptureSessionState.Faulted,
        CaptureSessionState.Reconnecting => next is CaptureSessionState.DeviceReady or CaptureSessionState.Starting or CaptureSessionState.Stopping or CaptureSessionState.Faulted,
        CaptureSessionState.Stopping => next is CaptureSessionState.Idle or CaptureSessionState.DeviceReady or CaptureSessionState.Faulted,
        CaptureSessionState.Faulted => next is CaptureSessionState.Idle or CaptureSessionState.Enumerating or CaptureSessionState.DeviceReady,
        _ => false
    };
}

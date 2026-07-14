namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed record PreviewCleanupStep(
    string Name,
    Action Execute,
    Func<Exception, bool>? IsExpectedFailure = null);

internal static class PreviewSessionCleanup
{
    public static void Execute(
        IEnumerable<PreviewCleanupStep> steps,
        Action<string, Exception> reportFailure,
        Action disposeCancellation,
        Action signalCompletion)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(disposeCancellation);
        ArgumentNullException.ThrowIfNull(signalCompletion);

        try
        {
            foreach (var step in steps) TryExecute(step, reportFailure);
        }
        finally
        {
            TryExecute(new PreviewCleanupStep("cancellation disposal", disposeCancellation), reportFailure);
            // TaskCompletionSource.TrySetResult is the production action. Keeping it
            // outermost guarantees Stop cannot wait forever because an earlier step failed.
            signalCompletion();
        }
    }

    private static void TryExecute(PreviewCleanupStep step, Action<string, Exception> reportFailure)
    {
        try
        {
            step.Execute();
        }
        catch (Exception exception) when (step.IsExpectedFailure?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            try { reportFailure(step.Name, exception); }
            catch (Exception) { }
        }
    }
}

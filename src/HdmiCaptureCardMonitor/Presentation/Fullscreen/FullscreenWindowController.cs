namespace HdmiCaptureCardMonitor.Presentation.Fullscreen;

/// <summary>
/// Serializes fullscreen presentation changes independently of capture state.
/// Repeated same-direction requests share one worker. Priority exit requests may
/// supersede an entry in progress; a stale entry is restored before it can publish
/// fullscreen state.
/// </summary>
public sealed class FullscreenWindowController : IFullscreenWindowController
{
    private readonly object syncRoot = new();
    private readonly IFullscreenWindowAdapter adapter;
    private TaskCompletionSource<FullscreenTransitionResult>? workerCompletion;
    private FullscreenWindowSnapshot? snapshot;
    private bool desiredFullscreen;
    private bool isFullscreen;
    private bool isTransitioning;
    private bool disposing;
    private bool disposed;
    private long generation;
    private FullscreenExitReason pendingExitReason = FullscreenExitReason.User;

    public FullscreenWindowController(IFullscreenWindowAdapter adapter) =>
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public bool IsFullscreen { get { lock (syncRoot) return isFullscreen; } }
    public bool IsTransitioning { get { lock (syncRoot) return isTransitioning; } }
    public long Generation { get { lock (syncRoot) return generation; } }

    public event EventHandler<FullscreenControllerStateChangedEventArgs>? StateChanged;

    public Task<FullscreenTransitionResult> EnterAsync()
    {
        Task<FullscreenTransitionResult> task;
        FullscreenControllerStateChangedEventArgs? notification = null;
        lock (syncRoot)
        {
            if (disposing || disposed) return Task.FromResult(DisposedFailure());
            if (desiredFullscreen)
                return workerCompletion?.Task ?? Task.FromResult(FullscreenTransitionResult.NoChange);
            if (isFullscreen && !isTransitioning)
                return Task.FromResult(FullscreenTransitionResult.NoChange);

            desiredFullscreen = true;
            generation++;
            task = EnsureWorkerLocked(out notification);
        }

        Publish(notification);
        return task;
    }

    public Task<FullscreenTransitionResult> ExitAsync(FullscreenExitReason reason)
    {
        Task<FullscreenTransitionResult> task;
        FullscreenControllerStateChangedEventArgs? notification = null;
        lock (syncRoot)
        {
            if (disposed) return Task.FromResult(FullscreenTransitionResult.NoChange);
            pendingExitReason = reason;

            if (!desiredFullscreen && !isFullscreen)
                return workerCompletion?.Task ?? Task.FromResult(FullscreenTransitionResult.NoChange);

            // Unlike a repeated F11 entry request, an exit is priority input. It
            // advances the generation even while entry is awaiting the adapter.
            desiredFullscreen = false;
            generation++;
            task = EnsureWorkerLocked(out notification);
        }

        Publish(notification);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (disposing || disposed) return;
            disposing = true;
        }

        _ = await ExitAsync(FullscreenExitReason.Disposal);
        lock (syncRoot) disposed = true;
        GC.SuppressFinalize(this);
    }

    private Task<FullscreenTransitionResult> EnsureWorkerLocked(
        out FullscreenControllerStateChangedEventArgs? notification)
    {
        if (workerCompletion is not null)
        {
            notification = null;
            return workerCompletion.Task;
        }

        workerCompletion = new TaskCompletionSource<FullscreenTransitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        isTransitioning = true;
        notification = CreateStateChangedLocked();
        var completion = workerCompletion;
        _ = RunWorkerAsync(completion);
        return completion.Task;
    }

    private async Task RunWorkerAsync(TaskCompletionSource<FullscreenTransitionResult> completion)
    {
        // Make request publication deterministic and permit a priority exit to be
        // recorded before the first adapter operation begins.
        await Task.Yield();
        var finalResult = FullscreenTransitionResult.NoChange;

        try
        {
            while (true)
            {
                bool target;
                bool actual;
                long operationGeneration;
                FullscreenExitReason exitReason;
                lock (syncRoot)
                {
                    target = desiredFullscreen;
                    actual = isFullscreen;
                    operationGeneration = generation;
                    exitReason = pendingExitReason;
                }

                if (target == actual)
                {
                    FullscreenControllerStateChangedEventArgs notification;
                    lock (syncRoot)
                    {
                        // Recheck under the same lock that retires the worker so a
                        // request cannot attach to a worker that has already decided
                        // to finish.
                        if (desiredFullscreen != isFullscreen) continue;
                        isTransitioning = false;
                        workerCompletion = null;
                        notification = CreateStateChangedLocked();
                    }

                    Publish(notification);
                    completion.TrySetResult(finalResult);
                    return;
                }

                if (target)
                    finalResult = await EnterCoreAsync(operationGeneration);
                else
                    finalResult = await ExitCoreAsync(exitReason, operationGeneration);

                if (!finalResult.IsSuccess && finalResult.Disposition != FullscreenTransitionDisposition.Superseded)
                {
                    lock (syncRoot) desiredFullscreen = false;
                }
            }
        }
        catch (Exception exception)
        {
            var failure = FullscreenFailure.Unexpected(exception);
            finalResult = await TryFallbackAfterUnexpectedFailureAsync(failure);
            FullscreenControllerStateChangedEventArgs failureNotification;
            lock (syncRoot)
            {
                desiredFullscreen = false;
                isFullscreen = false;
                snapshot = null;
                isTransitioning = false;
                workerCompletion = null;
                failureNotification = CreateStateChangedLocked();
            }
            Publish(failureNotification);
            completion.TrySetResult(finalResult);
            return;
        }
    }

    private async ValueTask<FullscreenTransitionResult> EnterCoreAsync(long operationGeneration)
    {
        var captured = await adapter.CaptureSnapshotAsync(operationGeneration);
        if (!captured.IsSuccess)
            return FullscreenTransitionResult.Failed(captured.Failure ?? UnknownFailure("Window placement capture failed."));

        var localSnapshot = captured.Snapshot!;
        lock (syncRoot) snapshot = localSnapshot;
        var result = await adapter.EnterFullscreenAsync(localSnapshot, operationGeneration);

        var stale = false;
        lock (syncRoot)
            stale = generation != operationGeneration || !desiredFullscreen || disposing || disposed;

        if (!result.IsSuccess || stale)
        {
            var reason = stale ? PendingExitReason() : FullscreenExitReason.User;
            var rollback = await RestoreOrFallbackAsync(localSnapshot, reason, operationGeneration);
            lock (syncRoot)
            {
                isFullscreen = false;
                snapshot = null;
            }

            if (stale && rollback.IsSuccess) return FullscreenTransitionResult.Superseded;
            if (!rollback.IsSuccess) return rollback;
            return result.IsSuccess
                ? FullscreenTransitionResult.Superseded
                : new FullscreenTransitionResult(false, FullscreenTransitionDisposition.RolledBack, result.Failure);
        }

        FullscreenControllerStateChangedEventArgs notification;
        lock (syncRoot)
        {
            isFullscreen = true;
            notification = CreateStateChangedLocked();
        }
        Publish(notification);
        return result;
    }

    private async ValueTask<FullscreenTransitionResult> ExitCoreAsync(
        FullscreenExitReason reason,
        long operationGeneration)
    {
        FullscreenWindowSnapshot? localSnapshot;
        lock (syncRoot) localSnapshot = snapshot;

        if (localSnapshot is null)
        {
            lock (syncRoot) isFullscreen = false;
            return FullscreenTransitionResult.NoChange;
        }

        var result = await RestoreOrFallbackAsync(localSnapshot, reason, operationGeneration);
        FullscreenControllerStateChangedEventArgs notification;
        lock (syncRoot)
        {
            isFullscreen = false;
            snapshot = null;
            notification = CreateStateChangedLocked();
        }
        Publish(notification);
        return result;
    }

    private async ValueTask<FullscreenTransitionResult> RestoreOrFallbackAsync(
        FullscreenWindowSnapshot localSnapshot,
        FullscreenExitReason reason,
        long operationGeneration)
    {
        FullscreenTransitionResult restore;
        try
        {
            restore = await adapter.RestoreWindowAsync(localSnapshot, reason, operationGeneration);
        }
        catch (Exception exception)
        {
            restore = FullscreenTransitionResult.Failed(FullscreenFailure.Unexpected(exception));
        }

        if (restore.IsSuccess) return restore;

        try
        {
            var fallback = await adapter.ApplySafeWindowedFallbackAsync(localSnapshot, reason, operationGeneration);
            return fallback.IsSuccess
                ? FullscreenTransitionResult.Fallback(restore.Failure ?? UnknownFailure("Exact window restoration failed."))
                : FullscreenTransitionResult.Fallback(fallback.Failure ?? restore.Failure ?? UnknownFailure("Window fallback failed."));
        }
        catch (Exception exception)
        {
            return FullscreenTransitionResult.Fallback(FullscreenFailure.Unexpected(exception));
        }
    }

    private async ValueTask<FullscreenTransitionResult> TryFallbackAfterUnexpectedFailureAsync(FullscreenFailure failure)
    {
        FullscreenWindowSnapshot? localSnapshot;
        long currentGeneration;
        lock (syncRoot)
        {
            localSnapshot = snapshot;
            currentGeneration = generation;
        }

        try
        {
            var fallback = await adapter.ApplySafeWindowedFallbackAsync(
                localSnapshot,
                PendingExitReason(),
                currentGeneration);
            return fallback.IsSuccess
                ? FullscreenTransitionResult.Fallback(failure)
                : FullscreenTransitionResult.Fallback(fallback.Failure ?? failure);
        }
        catch
        {
            return FullscreenTransitionResult.Fallback(failure);
        }
    }

    private FullscreenExitReason PendingExitReason()
    {
        lock (syncRoot) return pendingExitReason;
    }

    private FullscreenControllerStateChangedEventArgs CreateStateChangedLocked() =>
        new(isFullscreen, isTransitioning, generation);

    private void Publish(FullscreenControllerStateChangedEventArgs? eventArgs)
    {
        if (eventArgs is null) return;
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler<FullscreenControllerStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, eventArgs); }
            catch
            {
                // A presentation-state observer must not break rollback, cleanup,
                // or leave an async command faulted and unobserved.
            }
        }
    }

    private static FullscreenTransitionResult DisposedFailure() => FullscreenTransitionResult.Failed(
        new FullscreenFailure(
            "Fullscreen is unavailable because the window is closing.",
            "A fullscreen entry was requested after controller disposal."));

    private static FullscreenFailure UnknownFailure(string technicalMessage) => new(
        "Fullscreen could not be opened. Live preview is still running.",
        technicalMessage);
}

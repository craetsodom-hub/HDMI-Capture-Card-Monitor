namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed class ActiveOperationRegistry : IDisposable
{
    private readonly object lifecycleLock = new();
    private readonly CancellationTokenSource shutdownCancellation = new();
    private readonly HashSet<Task> activeOperations = [];
    private readonly TimeSpan shutdownTimeout;
    private bool acceptingOperations = true;
    private bool cancellationDisposed;

    public ActiveOperationRegistry(TimeSpan? shutdownTimeout = null) =>
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(3);

    public bool WorkersSettled { get; private set; } = true;

    public Task<T>? TryStart<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (lifecycleLock)
        {
            if (!acceptingOperations) return null;

            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCancellation.Token);
            var task = Task.Run(() => operation(linkedCancellation.Token), CancellationToken.None);
            activeOperations.Add(task);
            _ = task.ContinueWith(
                completed => Complete(completed, linkedCancellation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    public void Dispose()
    {
        Task[] snapshot;
        lock (lifecycleLock)
        {
            acceptingOperations = false;
            if (!cancellationDisposed && !shutdownCancellation.IsCancellationRequested) shutdownCancellation.Cancel();
            snapshot = [.. activeOperations];
        }

        WorkersSettled = WaitForOperations(snapshot);
        lock (lifecycleLock)
        {
            if (activeOperations.Count == 0 && !cancellationDisposed)
            {
                shutdownCancellation.Dispose();
                cancellationDisposed = true;
            }
        }
    }

    private bool WaitForOperations(Task[] operations)
    {
        if (operations.Length == 0) return true;
        try { return Task.WaitAll(operations, shutdownTimeout); }
        catch (AggregateException) { return operations.All(operation => operation.IsCompleted); }
    }

    private void Complete(Task operation, CancellationTokenSource linkedCancellation)
    {
        linkedCancellation.Dispose();
        lock (lifecycleLock)
        {
            activeOperations.Remove(operation);
            if (!acceptingOperations && activeOperations.Count == 0 && !cancellationDisposed)
            {
                shutdownCancellation.Dispose();
                cancellationDisposed = true;
            }
        }
    }
}

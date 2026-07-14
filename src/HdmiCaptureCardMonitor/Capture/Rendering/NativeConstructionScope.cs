namespace HdmiCaptureCardMonitor.Capture.Rendering;

internal sealed class NativeConstructionScope : IDisposable
{
    private readonly Stack<Action> releases = new();
    private bool committed;
    private bool disposed;

    public T Own<T>(T resource, Action<T> release) where T : class
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(release);
        releases.Push(() => release(resource));
        return resource;
    }

    public void Commit()
    {
        committed = true;
        releases.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (committed) return;

        while (releases.TryPop(out var release))
        {
            try { release(); }
            catch (Exception) { }
        }
    }
}

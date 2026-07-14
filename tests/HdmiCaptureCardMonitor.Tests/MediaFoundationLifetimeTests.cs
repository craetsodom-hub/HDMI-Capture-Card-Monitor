using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class MediaFoundationLifetimeTests
{
    [Theory]
    [MemberData(nameof(StartupFailures))]
    public void StartupFailuresAreClassifiedExactly(int hresult, MediaFoundationStartupStatus expected) =>
        Assert.Equal(expected, MediaFoundationStartupClassifier.Classify(hresult));

    public static IEnumerable<object[]> StartupFailures =>
    [
        [MediaFoundationHResults.ENotImplemented, MediaFoundationStartupStatus.MissingMediaComponents],
        [MediaFoundationHResults.BadStartupVersion, MediaFoundationStartupStatus.UnsupportedStartupVersion],
        [MediaFoundationHResults.DisabledInSafeMode, MediaFoundationStartupStatus.DisabledInSafeMode],
        [unchecked((int)0xC00D36B0), MediaFoundationStartupStatus.OtherFailure],
        [unchecked((int)0x80004005), MediaFoundationStartupStatus.OtherFailure]
    ];

    [Fact]
    public void InitializeCachesTheExactOriginalResult()
    {
        var calls = 0;
        using var runtime = new MediaFoundationRuntime(() => { calls++; return MediaFoundationHResults.ENotImplemented; }, () => 0, NullApplicationLogger.Instance);

        var first = runtime.Initialize();
        var second = runtime.Initialize();

        Assert.Same(first, second);
        Assert.Equal(MediaFoundationHResults.ENotImplemented, second.HResult);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InitializeAfterDisposalReturnsDeterministicFailure()
    {
        var calls = 0;
        var runtime = new MediaFoundationRuntime(() => { calls++; return 0; }, () => 0, NullApplicationLogger.Instance);
        runtime.Dispose();

        var first = runtime.Initialize();
        var second = runtime.Initialize();

        Assert.Same(first, second);
        Assert.Equal(MediaFoundationStartupStatus.OtherFailure, first.Status);
        Assert.Null(first.HResult);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FailedShutdownIsLoggedAndDisposeIsIdempotent()
    {
        var logger = new RecordingLogger();
        var shutdownCalls = 0;
        var runtime = new MediaFoundationRuntime(() => 0, () => { shutdownCalls++; return unchecked((int)0x80004005); }, logger);
        Assert.True(runtime.Initialize().IsSuccess);

        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, shutdownCalls);
        Assert.Contains(logger.Warnings, warning => warning.Contains("0x80004005", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperationStartedBeforeDisposeSettlesInsideTimeout()
    {
        using var registry = new ActiveOperationRegistry(TimeSpan.FromSeconds(2));
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        var operation = registry.TryStart(_ => { entered.TrySetResult(); release.Wait(CancellationToken.None); return 1; }, CancellationToken.None)!;
        await entered.Task;

        var disposal = Task.Run(registry.Dispose);
        release.Set();
        await disposal;

        Assert.Equal(1, await operation);
        Assert.True(registry.WorkersSettled);
    }

    [Fact]
    public async Task ConcurrentOperationIsRejectedAfterShutdownBegins()
    {
        using var registry = new ActiveOperationRegistry(TimeSpan.FromSeconds(2));
        var cancellationObserved = NewSignal();
        using var release = new ManualResetEventSlim();
        var first = registry.TryStart(token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            release.Wait(CancellationToken.None);
            return 1;
        }, CancellationToken.None)!;

        var disposal = Task.Run(registry.Dispose);
        await cancellationObserved.Task;
        var rejected = registry.TryStart(_ => 2, CancellationToken.None);
        release.Set();
        await disposal;
        await first;

        Assert.Null(rejected);
    }

    [Fact]
    public async Task MultipleBlockedOperationsAreTrackedAndSettled()
    {
        using var registry = new ActiveOperationRegistry(TimeSpan.FromSeconds(2));
        var entered = new CountdownEvent(3);
        using var release = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 3).Select(index => registry.TryStart(_ => { entered.Signal(); release.Wait(CancellationToken.None); return index; }, CancellationToken.None)!).ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var disposal = Task.Run(registry.Dispose);
        release.Set();
        await disposal;

        Assert.Equal([0, 1, 2], await Task.WhenAll(tasks));
        Assert.True(registry.WorkersSettled);
    }

    [Fact]
    public async Task TimeoutDoesNotDisposeResourcesStillReferencedByWorker()
    {
        var registry = new ActiveOperationRegistry(TimeSpan.Zero);
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        var operation = registry.TryStart(token => { entered.TrySetResult(); release.Wait(CancellationToken.None); return token.IsCancellationRequested; }, CancellationToken.None)!;
        await entered.Task;

        registry.Dispose();
        Assert.False(registry.WorkersSettled);
        Assert.Null(registry.TryStart(_ => false, CancellationToken.None));

        release.Set();
        Assert.True(await operation);
        registry.Dispose();
        registry.Dispose();
        Assert.True(registry.WorkersSettled);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingLogger : IApplicationLogger
    {
        public List<string> Warnings { get; } = [];
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void LogError(string message, Exception? exception = null) { }
    }
}

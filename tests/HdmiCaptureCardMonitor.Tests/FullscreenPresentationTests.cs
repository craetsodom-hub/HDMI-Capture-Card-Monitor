using HdmiCaptureCardMonitor.Presentation.Fullscreen;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class FullscreenPresentationTests
{
    private static readonly FullscreenFailure EntryFailure = FullscreenFailure.Create(
        FullscreenOperation.Entry,
        "Synthetic entry failure.");

    [Fact]
    public void SnapshotKeepsNativePixelsAndNegativeMonitorCoordinates()
    {
        var snapshot = CreateSnapshot();

        Assert.Equal(new FullscreenRectangle(-1920, -120, 0, 960), snapshot.MonitorBounds);
        Assert.Equal(-1820, snapshot.Placement.NormalPosition.Left);
        Assert.Equal(150u, snapshot.Dpi);
        Assert.Equal((nint)0x10CF0000, snapshot.NativeStyle.Style);
        Assert.True(snapshot.Topmost);
    }

    [Fact]
    public async Task EnterAndExitUseOneSnapshotAndAreIdempotent()
    {
        var adapter = new FakeWindowAdapter();
        var controller = new FullscreenWindowController(adapter);

        var entered = await controller.EnterAsync();
        var duplicateEnter = await controller.EnterAsync();
        var exited = await controller.ExitAsync(FullscreenExitReason.Escape);
        var duplicateExit = await controller.ExitAsync(FullscreenExitReason.User);

        Assert.True(entered.IsSuccess);
        Assert.Equal(FullscreenTransitionDisposition.NoChange, duplicateEnter.Disposition);
        Assert.True(exited.IsSuccess);
        Assert.Equal(FullscreenTransitionDisposition.NoChange, duplicateExit.Disposition);
        Assert.False(controller.IsFullscreen);
        Assert.False(controller.IsTransitioning);
        Assert.Equal(1, adapter.CaptureCalls);
        Assert.Equal(1, adapter.EnterCalls);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Same(adapter.CapturedSnapshot, adapter.RestoredSnapshots.Single());
        Assert.Equal(FullscreenExitReason.Escape, adapter.RestoreReasons.Single());
    }

    [Fact]
    public async Task RepeatedEntryDuringTransitionSharesOneSerializedWorker()
    {
        var adapter = new FakeWindowAdapter { BlockEntry = true };
        var controller = new FullscreenWindowController(adapter);

        var first = controller.EnterAsync();
        await adapter.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = controller.EnterAsync();

        Assert.Same(first, second);
        Assert.True(controller.IsTransitioning);
        Assert.Equal(1, adapter.EnterCalls);

        adapter.ReleaseEntry();
        Assert.True((await first).IsSuccess);
        Assert.True(controller.IsFullscreen);
        Assert.Equal(1, adapter.CaptureCalls);
        Assert.Equal(1, adapter.EnterCalls);
    }

    [Fact]
    public async Task PriorityExitDuringEntrySupersedesStaleCompletionAndRestores()
    {
        var adapter = new FakeWindowAdapter { BlockEntry = true };
        var controller = new FullscreenWindowController(adapter);
        var fullscreenPublished = 0;
        controller.StateChanged += (_, args) =>
        {
            if (args.IsFullscreen) fullscreenPublished++;
        };

        var entry = controller.EnterAsync();
        await adapter.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var generationDuringEntry = controller.Generation;
        var exit = controller.ExitAsync(FullscreenExitReason.Stop);
        Assert.True(controller.Generation > generationDuringEntry);

        adapter.ReleaseEntry();
        var entryResult = await entry;
        var exitResult = await exit;

        Assert.Same(entry, exit);
        Assert.Equal(FullscreenTransitionDisposition.Superseded, entryResult.Disposition);
        Assert.Equal(entryResult, exitResult);
        Assert.False(controller.IsFullscreen);
        Assert.False(controller.IsTransitioning);
        Assert.Equal(0, fullscreenPublished);
        Assert.Equal(1, adapter.EnterCalls);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Equal(FullscreenExitReason.Stop, adapter.RestoreReasons.Single());
    }

    [Fact]
    public async Task EntryFailureRollsBackWithoutPublishingFullscreen()
    {
        var adapter = new FakeWindowAdapter
        {
            EnterResult = FullscreenTransitionResult.Failed(EntryFailure)
        };
        var controller = new FullscreenWindowController(adapter);

        var result = await controller.EnterAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(FullscreenTransitionDisposition.RolledBack, result.Disposition);
        Assert.Same(EntryFailure, result.Failure);
        Assert.Equal(
            "Fullscreen could not be opened. Live preview is still running.",
            result.Failure!.CustomerMessage);
        Assert.False(controller.IsFullscreen);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Equal(0, adapter.FallbackCalls);
    }

    [Fact]
    public async Task FailedExactRestoreUsesSafeVisibleFallback()
    {
        var restoreFailure = FullscreenFailure.Create(
            FullscreenOperation.ExactRestore,
            "Synthetic exact-restore failure.");
        var adapter = new FakeWindowAdapter
        {
            RestoreResult = FullscreenTransitionResult.Failed(restoreFailure)
        };
        var controller = new FullscreenWindowController(adapter);
        await controller.EnterAsync();

        var result = await controller.ExitAsync(FullscreenExitReason.DisplayRemoved);

        Assert.False(result.IsSuccess);
        Assert.True(result.UsedSafeFallback);
        Assert.Same(restoreFailure, result.Failure);
        Assert.False(controller.IsFullscreen);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Equal(1, adapter.FallbackCalls);
        Assert.Equal(FullscreenExitReason.DisplayRemoved, adapter.FallbackReasons.Single());
        Assert.Equal(
            "The previous window position could not be restored exactly. Live preview is still running in a safe window.",
            result.Failure!.CustomerMessage);
    }

    [Fact]
    public async Task FailedExactRestoreAndFallbackUseExitFailureWording()
    {
        var adapter = new FakeWindowAdapter
        {
            RestoreResult = FullscreenTransitionResult.Failed(FullscreenFailure.Create(
                FullscreenOperation.ExactRestore,
                "Synthetic exact-restore failure.")),
            FallbackResult = FullscreenTransitionResult.Failed(FullscreenFailure.Create(
                FullscreenOperation.SafeFallback,
                "Synthetic fallback failure."))
        };
        var controller = new FullscreenWindowController(adapter);
        await controller.EnterAsync();

        var result = await controller.ExitAsync(FullscreenExitReason.User);

        Assert.False(result.IsSuccess);
        Assert.True(result.UsedSafeFallback);
        Assert.Equal(
            "Fullscreen could not be closed normally. Live preview is still running.",
            result.Failure!.CustomerMessage);
    }

    [Fact]
    public async Task RepeatedCyclesCaptureFreshPlacementWithoutDriftInController()
    {
        var adapter = new FakeWindowAdapter();
        var controller = new FullscreenWindowController(adapter);

        for (var cycle = 0; cycle < 30; cycle++)
        {
            Assert.True((await controller.EnterAsync()).IsSuccess);
            Assert.True(controller.IsFullscreen);
            Assert.True((await controller.ExitAsync(FullscreenExitReason.User)).IsSuccess);
            Assert.False(controller.IsFullscreen);
        }

        Assert.Equal(30, adapter.CaptureCalls);
        Assert.Equal(30, adapter.EnterCalls);
        Assert.Equal(30, adapter.RestoreCalls);
        Assert.Equal(0, adapter.FallbackCalls);
        Assert.All(adapter.RestoredSnapshots, value => Assert.Same(adapter.CapturedSnapshot, value));
    }

    [Fact]
    public async Task AdapterExceptionCannotEscapeAndFallsBackWindowed()
    {
        var adapter = new FakeWindowAdapter { ThrowDuringEntry = true };
        var controller = new FullscreenWindowController(adapter);

        var result = await controller.EnterAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.UsedSafeFallback);
        Assert.False(controller.IsFullscreen);
        Assert.False(controller.IsTransitioning);
        Assert.Equal(1, adapter.FallbackCalls);
        Assert.NotNull(result.Failure?.Exception);
    }

    [Fact]
    public async Task DisposalRestoresFullscreenBeforeRejectingNewEntry()
    {
        var adapter = new FakeWindowAdapter();
        var controller = new FullscreenWindowController(adapter);
        await controller.EnterAsync();

        await controller.DisposeAsync();
        var result = await controller.EnterAsync();

        Assert.False(controller.IsFullscreen);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Equal(FullscreenExitReason.Disposal, adapter.RestoreReasons.Single());
        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Fullscreen could not be closed normally. Live preview is still running.",
            result.Failure!.CustomerMessage);
    }

    [Fact]
    public async Task DisposalDuringEntrySupersedesCompletionAndWaitsForRestoration()
    {
        var adapter = new FakeWindowAdapter { BlockEntry = true };
        var controller = new FullscreenWindowController(adapter);
        var entry = controller.EnterAsync();
        await adapter.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = controller.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        adapter.ReleaseEntry();

        var entryResult = await entry;
        await disposal;

        Assert.Equal(FullscreenTransitionDisposition.Superseded, entryResult.Disposition);
        Assert.False(controller.IsFullscreen);
        Assert.False(controller.IsTransitioning);
        Assert.Equal(1, adapter.RestoreCalls);
        Assert.Equal(FullscreenExitReason.Disposal, adapter.RestoreReasons.Single());
        Assert.False((await controller.EnterAsync()).IsSuccess);
    }

    [Fact]
    public async Task SnapshotCaptureFailureDoesNotMutateOrInvokeFallback()
    {
        var adapter = new FakeWindowAdapter { FailCapture = true };
        var controller = new FullscreenWindowController(adapter);

        var result = await controller.EnterAsync();

        Assert.False(result.IsSuccess);
        Assert.False(controller.IsFullscreen);
        Assert.Equal(1, adapter.CaptureCalls);
        Assert.Equal(0, adapter.EnterCalls);
        Assert.Equal(0, adapter.RestoreCalls);
        Assert.Equal(0, adapter.FallbackCalls);
    }

    [Fact]
    public void CursorBeginsVisibleAndHidesAfterDeterministicInactivity()
    {
        var timer = new FakeInactivityTimer();
        var sink = new FakeCursorSink();
        using var controller = new FullscreenCursorController(timer, sink);

        controller.EnterFullscreenPreview();
        Assert.False(controller.IsHidden);
        Assert.Equal(FullscreenCursorController.DefaultInactivityDelay, timer.DueTime);
        Assert.False(sink.HiddenStates.Last());

        timer.FireCurrent();

        Assert.True(controller.IsHidden);
        Assert.True(sink.HiddenStates.Last());
    }

    [Fact]
    public void PointerMovementRestoresCursorAndInvalidatesOlderTimerCallback()
    {
        var timer = new FakeInactivityTimer();
        var sink = new FakeCursorSink();
        using var controller = new FullscreenCursorController(timer, sink);
        controller.EnterFullscreenPreview();
        var staleCallback = timer.CurrentCallback!;
        timer.FireCurrent();
        Assert.True(controller.IsHidden);

        controller.NotifyPointerActivity();
        Assert.False(controller.IsHidden);
        var currentCallback = timer.CurrentCallback!;

        staleCallback();
        Assert.False(controller.IsHidden);
        currentCallback();
        Assert.True(controller.IsHidden);
    }

    [Fact]
    public void CursorStaysVisibleThroughoutTransitionAndRearmsAfterward()
    {
        var timer = new FakeInactivityTimer();
        var sink = new FakeCursorSink();
        using var controller = new FullscreenCursorController(timer, sink);
        controller.EnterFullscreenPreview();
        timer.FireCurrent();
        Assert.True(controller.IsHidden);

        controller.SetTransitioning(true);
        Assert.False(controller.IsHidden);
        Assert.Equal(1, timer.StopCalls);
        var stoppedCallback = timer.CurrentCallback;
        stoppedCallback?.Invoke();
        Assert.False(controller.IsHidden);

        controller.SetTransitioning(false);
        Assert.True(timer.RestartCalls >= 2);
        timer.FireCurrent();
        Assert.True(controller.IsHidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitAndFailureAlwaysRestoreCursor(bool failure)
    {
        var timer = new FakeInactivityTimer();
        var sink = new FakeCursorSink();
        using var controller = new FullscreenCursorController(timer, sink);
        controller.EnterFullscreenPreview();
        timer.FireCurrent();

        if (failure) controller.RestoreForFailure();
        else controller.ExitFullscreen();

        Assert.False(controller.IsHidden);
        Assert.False(sink.HiddenStates.Last());
        Assert.True(timer.StopCalls > 0);
        timer.CurrentCallback?.Invoke();
        Assert.False(controller.IsHidden);
    }

    [Fact]
    public void DisposalRestoresCursorAndDisposesTimerIdempotently()
    {
        var timer = new FakeInactivityTimer();
        var sink = new FakeCursorSink();
        var controller = new FullscreenCursorController(timer, sink);
        controller.EnterFullscreenPreview();
        timer.FireCurrent();

        controller.Dispose();
        controller.Dispose();
        controller.NotifyPointerActivity();

        Assert.False(controller.IsHidden);
        Assert.False(sink.HiddenStates.Last());
        Assert.Equal(1, timer.DisposeCalls);
    }

    [Fact]
    public void FullscreenCoreDoesNotUseShowCursorCounterOrPolling()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "Presentation", "Fullscreen");
        var source = string.Join('\n', Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("ShowCursor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCursorPos", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
    }

    private static FullscreenWindowSnapshot CreateSnapshot() => new(
        new FullscreenWindowPlacement(
            0,
            3,
            new FullscreenPoint(-1, -1),
            new FullscreenPoint(-1, -1),
            new FullscreenRectangle(-1820, 40, -620, 840)),
        new FullscreenNativeStyle((nint)0x10CF0000, (nint)0x00040100),
        WindowStyle: 1,
        ResizeMode: 2,
        WindowState: 2,
        Topmost: true,
        MonitorHandle: (nint)7,
        MonitorBounds: new FullscreenRectangle(-1920, -120, 0, 960),
        MonitorWorkArea: new FullscreenRectangle(-1920, -80, 0, 920),
        Dpi: 150);

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HDMI-Capture-Card-Monitor.sln"))) return directory;
        }
        throw new DirectoryNotFoundException("The test could not locate the repository root.");
    }

    private sealed class FakeWindowAdapter : IFullscreenWindowAdapter
    {
        private readonly TaskCompletionSource<bool> entryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FullscreenWindowSnapshot CapturedSnapshot { get; } = CreateSnapshot();
        public FullscreenTransitionResult EnterResult { get; set; } = FullscreenTransitionResult.Applied;
        public FullscreenTransitionResult RestoreResult { get; set; } = FullscreenTransitionResult.Applied;
        public FullscreenTransitionResult FallbackResult { get; set; } = FullscreenTransitionResult.Applied;
        public bool BlockEntry { get; set; }
        public bool FailCapture { get; set; }
        public bool ThrowDuringEntry { get; set; }
        public int CaptureCalls { get; private set; }
        public int EnterCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int FallbackCalls { get; private set; }
        public TaskCompletionSource<bool> EntryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FullscreenWindowSnapshot> RestoredSnapshots { get; } = [];
        public List<FullscreenExitReason> RestoreReasons { get; } = [];
        public List<FullscreenExitReason> FallbackReasons { get; } = [];

        public ValueTask<FullscreenSnapshotResult> CaptureSnapshotAsync(long generation)
        {
            _ = generation;
            CaptureCalls++;
            if (FailCapture)
                return ValueTask.FromResult(FullscreenSnapshotResult.Failed(EntryFailure));
            return ValueTask.FromResult(FullscreenSnapshotResult.Captured(CapturedSnapshot));
        }

        public async ValueTask<FullscreenTransitionResult> EnterFullscreenAsync(
            FullscreenWindowSnapshot snapshot,
            long generation)
        {
            _ = snapshot;
            _ = generation;
            EnterCalls++;
            EntryStarted.TrySetResult(true);
            if (BlockEntry) await entryRelease.Task;
            if (ThrowDuringEntry) throw new InvalidOperationException("Synthetic adapter exception.");
            return EnterResult;
        }

        public ValueTask<FullscreenTransitionResult> RestoreWindowAsync(
            FullscreenWindowSnapshot snapshot,
            FullscreenExitReason reason,
            long generation)
        {
            _ = generation;
            RestoreCalls++;
            RestoredSnapshots.Add(snapshot);
            RestoreReasons.Add(reason);
            return ValueTask.FromResult(RestoreResult);
        }

        public ValueTask<FullscreenTransitionResult> ApplySafeWindowedFallbackAsync(
            FullscreenWindowSnapshot? snapshot,
            FullscreenExitReason reason,
            long generation)
        {
            _ = snapshot;
            _ = generation;
            FallbackCalls++;
            FallbackReasons.Add(reason);
            return ValueTask.FromResult(FallbackResult);
        }

        public void ReleaseEntry() => entryRelease.TrySetResult(true);
    }

    private sealed class FakeInactivityTimer : IFullscreenInactivityTimer
    {
        public TimeSpan DueTime { get; private set; }
        public Action? CurrentCallback { get; private set; }
        public int RestartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Restart(TimeSpan dueTime, Action callback)
        {
            DueTime = dueTime;
            CurrentCallback = callback;
            RestartCalls++;
        }

        public void StopTimer() => StopCalls++;
        public void Dispose() => DisposeCalls++;
        public void FireCurrent() => CurrentCallback?.Invoke();
    }

    private sealed class FakeCursorSink : IFullscreenCursorSink
    {
        public List<bool> HiddenStates { get; } = [];
        public void SetFullscreenCursorHidden(bool hidden) => HiddenStates.Add(hidden);
    }
}

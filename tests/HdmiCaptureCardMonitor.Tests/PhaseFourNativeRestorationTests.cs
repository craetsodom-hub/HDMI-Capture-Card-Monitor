using HdmiCaptureCardMonitor.Presentation.Fullscreen;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseFourNativeRestorationTests
{
    [Fact]
    public void NonZeroPreviousStyleIsSuccessfulAndPreservesSignedPointerValue()
    {
        var signedValue = unchecked((nint)(long)0xFFFFFFFF80000000);
        var interop = new FakeStyleInterop { Result = signedValue, Error = 5 };
        var result = new NativeWindowStyleAccessor(interop).Read((nint)7, NativeWindowStyleKind.Style);

        Assert.True(result.IsSuccess);
        Assert.Equal(signedValue, result.Value);
        Assert.Equal(1, interop.ClearCalls);
        Assert.Equal(0, interop.GetErrorCalls);
    }

    [Fact]
    public void ZeroPreviousStyleWithNoErrorIsSuccessful()
    {
        var interop = new FakeStyleInterop { Result = 0, Error = 0 };
        var result = new NativeWindowStyleAccessor(interop).Read((nint)7, NativeWindowStyleKind.Style);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, interop.ClearCalls);
        Assert.Equal(1, interop.GetErrorCalls);
    }

    [Fact]
    public void ZeroStyleResultWithNativeErrorFails()
    {
        var interop = new FakeStyleInterop { Result = 0, Error = 5 };
        var result = new NativeWindowStyleAccessor(interop).Write(
            (nint)7,
            NativeWindowStyleKind.ExtendedStyle,
            unchecked((nint)(long)0xFFFFFFFF80000000));

        Assert.False(result.IsSuccess);
        Assert.Equal(5u, result.ErrorCode);
        Assert.Equal(1, interop.ClearCalls);
        Assert.Equal(1, interop.GetErrorCalls);
        Assert.Equal(NativeWindowStyleKind.ExtendedStyle, interop.LastKind);
    }

    [Theory]
    [InlineData(false, true, true, true, "normal style")]
    [InlineData(true, false, true, true, "extended style")]
    [InlineData(true, true, false, true, "window placement")]
    [InlineData(true, true, true, false, "frame refresh")]
    public void EveryRequiredRestorationStepControlsExactSuccess(
        bool style,
        bool extendedStyle,
        bool placement,
        bool frame,
        string failedStep)
    {
        var result = WindowRestorationResult.Execute(
            () => Access(style, 5),
            () => Access(extendedStyle, 6),
            () => placement,
            () => frame);

        Assert.False(result.IsSuccess);
        Assert.Contains(failedStep, result.FailureSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void LaterRestorationOperationsAreAttemptedAfterEarlierFailures()
    {
        var calls = new List<string>();
        var result = WindowRestorationResult.Execute(
            () => { calls.Add("style"); return Access(false, 5); },
            () => { calls.Add("extended-style"); return Access(false, 6); },
            () => { calls.Add("placement"); return false; },
            () => { calls.Add("frame"); return false; });

        Assert.False(result.IsSuccess);
        Assert.Equal(["style", "extended-style", "placement", "frame"], calls);
        Assert.Equal(5u, result.FirstNativeError);
    }

    [Fact]
    public void ExactRestorationRequiresEveryOperationToSucceed()
    {
        var result = WindowRestorationResult.Execute(
            () => Access(true),
            () => Access(true),
            () => true,
            () => true);

        Assert.True(result.IsSuccess);
        Assert.True(result.StyleRestored);
        Assert.True(result.ExtendedStyleRestored);
        Assert.True(result.PlacementRestored);
        Assert.True(result.FrameRefreshed);
    }

    [Fact]
    public void FullscreenFailureWordingIsOperationSpecificAndContainsNoNativeDetails()
    {
        var entry = FullscreenFailure.Create(FullscreenOperation.Entry, "entry", 5);
        var exactRestore = FullscreenFailure.Create(FullscreenOperation.ExactRestore, "restore", 6);
        var fallback = FullscreenFailure.Create(FullscreenOperation.SafeFallback, "fallback", 7);

        Assert.Equal("Fullscreen could not be opened. Live preview is still running.", entry.CustomerMessage);
        Assert.Equal(
            "The previous window position could not be restored exactly. Live preview is still running in a safe window.",
            exactRestore.CustomerMessage);
        Assert.Equal("Fullscreen could not be closed normally. Live preview is still running.", fallback.CustomerMessage);
        Assert.All(
            new[] { entry, exactRestore, fallback },
            failure =>
            {
                Assert.DoesNotContain("5", failure.CustomerMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("HRESULT", failure.CustomerMessage, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("HWND", failure.CustomerMessage, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void OrdinaryWindowedCloseIsAllowedWithoutPreparation()
    {
        var coordinator = new WindowCloseCoordinator();

        Assert.Equal(WindowCloseDecision.AllowAndDispose, coordinator.Evaluate(false, false));
    }

    [Fact]
    public void FullscreenCloseIsCancelledOnceAndReissuedOnce()
    {
        var coordinator = new WindowCloseCoordinator();

        Assert.Equal(WindowCloseDecision.CancelAndPrepare, coordinator.Evaluate(true, false));
        Assert.Equal(WindowCloseDecision.CancelWhilePreparing, coordinator.Evaluate(true, true));
        Assert.True(coordinator.CompletePreparationAndRequestClose());
        Assert.False(coordinator.CompletePreparationAndRequestClose());
        Assert.Equal(WindowCloseDecision.AllowAndDispose, coordinator.Evaluate(false, false));
    }

    [Fact]
    public void CloseDuringEntryUsesTheSinglePreparationWorker()
    {
        var coordinator = new WindowCloseCoordinator();

        Assert.Equal(WindowCloseDecision.CancelAndPrepare, coordinator.Evaluate(false, true));
        Assert.Equal(WindowCloseDecision.CancelWhilePreparing, coordinator.Evaluate(false, true));
        Assert.True(coordinator.CompletePreparationAndRequestClose());
        Assert.Equal(WindowCloseDecision.AllowAndDispose, coordinator.Evaluate(false, false));
    }

    [Fact]
    public void ExitFailureCannotPreventPreparedClosing()
    {
        var coordinator = new WindowCloseCoordinator();
        Assert.Equal(WindowCloseDecision.CancelAndPrepare, coordinator.Evaluate(true, false));

        // Completion is a finally-path policy decision and is independent of the
        // fullscreen transition result.
        Assert.True(coordinator.CompletePreparationAndRequestClose());
        Assert.Equal(WindowCloseDecision.AllowAndDispose, coordinator.Evaluate(false, false));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, false)]
    public void DisplayChangeRequestsOnlyActiveFullscreenExit(
        bool disposed,
        bool fullscreen,
        bool transitioning,
        bool expected)
    {
        Assert.Equal(
            expected,
            FullscreenDisplayChangePolicy.ShouldRequestExit(disposed, fullscreen, transitioning));
    }

    [Fact]
    public async Task WindowedControllerDisposalCompletesSynchronouslyAndRepeatedly()
    {
        var controller = new FullscreenWindowController(new ImmediateWindowAdapter());

        var first = controller.DisposeAsync();
        var second = controller.DisposeAsync();

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        await first;
        await second;
    }

    private static NativeStyleAccessResult Access(bool success, uint error = 0) =>
        new(success, 0, error);

    private sealed class FakeStyleInterop : IWindowStyleInterop
    {
        public uint NoErrorCode => 0;
        public nint Result { get; set; }
        public uint Error { get; set; }
        public int ClearCalls { get; private set; }
        public int GetErrorCalls { get; private set; }
        public NativeWindowStyleKind LastKind { get; private set; }

        public void ClearLastError() => ClearCalls++;
        public uint GetLastError() { GetErrorCalls++; return Error; }
        public nint GetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind)
        {
            _ = windowHandle;
            LastKind = kind;
            return Result;
        }

        public nint SetWindowLongPtr(nint windowHandle, NativeWindowStyleKind kind, nint value)
        {
            _ = windowHandle;
            _ = value;
            LastKind = kind;
            return Result;
        }
    }

    private sealed class ImmediateWindowAdapter : IFullscreenWindowAdapter
    {
        public ValueTask<FullscreenSnapshotResult> CaptureSnapshotAsync(long generation) =>
            throw new NotSupportedException();
        public ValueTask<FullscreenTransitionResult> EnterFullscreenAsync(FullscreenWindowSnapshot snapshot, long generation) =>
            throw new NotSupportedException();
        public ValueTask<FullscreenTransitionResult> RestoreWindowAsync(FullscreenWindowSnapshot snapshot, FullscreenExitReason reason, long generation) =>
            throw new NotSupportedException();
        public ValueTask<FullscreenTransitionResult> ApplySafeWindowedFallbackAsync(FullscreenWindowSnapshot? snapshot, FullscreenExitReason reason, long generation) =>
            throw new NotSupportedException();
    }
}

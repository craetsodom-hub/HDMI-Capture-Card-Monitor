using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class AudioWorkerLifetimeTests
{
    private static readonly AudioEndpoint Capture = new("opaque-capture", "Test microphone", AudioEndpointDataFlow.Capture);

    [Fact]
    public async Task QueueControllerSettlesInsideStopTimeout()
    {
        using var controller = new AudioQueueRateController(
            stop => stop.WaitOne(), TimeSpan.FromSeconds(1));

        Assert.True(controller.Start());
        Assert.True(controller.Stop());
        await controller.Completion;
        Assert.True(controller.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task QueueControllerTimeoutLeavesCompletionPendingUntilWorkerActuallySettles()
    {
        using var release = new ManualResetEvent(false);
        using var controller = new AudioQueueRateController(
            stop => { _ = stop.WaitOne(); _ = release.WaitOne(); },
            TimeSpan.FromMilliseconds(20));

        Assert.True(controller.Start());
        Assert.False(controller.Stop());
        Assert.False(controller.Completion.IsCompleted);

        release.Set();
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(controller.Completion.IsCompletedSuccessfully);
        Assert.True(controller.HandlesReleased);
    }

    [Fact]
    public async Task TimedOutControllerKeepsServiceActiveRejectsRestartAndClearsAfterOwnershipRelease()
    {
        using var release = new ManualResetEvent(false);
        var session = new ControlledControllerSession(release);
        using var service = new WasapiAudioMonitorService(NullApplicationLogger.Instance, _ => session);
        var request = new AudioMonitorStartRequest(Capture, null);

        Assert.True((await service.StartAsync(request)).IsSuccess);
        var stop = await service.StopAsync();
        Assert.True(stop.TimedOut);
        Assert.True(service.IsActive);
        Assert.False(service.WorkersSettled);
        Assert.False((await service.StartAsync(request)).IsSuccess);
        Assert.False(session.OwnershipReleased);

        release.Set();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        WaitUntil(() => !service.IsActive);
        Assert.True(session.OwnershipReleased);
        Assert.True(service.WorkersSettled);
        Assert.True((await service.StopAsync()).IsSuccess);
    }

    [Fact]
    public void ThrowingEventSubscriberDoesNotPreventLaterSubscriber()
    {
        var delivered = 0;
        EventHandler<AudioMonitorStateChangedEventArgs> handlers = (_, _) => throw new InvalidOperationException("observer");
        handlers += (_, _) => delivered++;

        SafeAudioEventDispatch.Publish(
            handlers,
            this,
            new AudioMonitorStateChangedEventArgs(Guid.NewGuid(), AudioMonitorState.Starting, AudioMonitorState.Monitoring),
            NullApplicationLogger.Instance,
            "test");

        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task DiagnosticsPublisherUsesNonProducerThreadAndSettlesOnStop()
    {
        var producerThread = Environment.CurrentManagedThreadId;
        var deliveredThread = 0;
        using var delivered = new ManualResetEventSlim(false);
        using var publisher = new LatestAudioDiagnosticsPublisher(_ =>
        {
            deliveredThread = Environment.CurrentManagedThreadId;
            delivered.Set();
        });

        publisher.Start();
        publisher.PublishLatest(CreateDiagnosticsEventArgs());
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(1)));
        Assert.NotEqual(producerThread, deliveredThread);
        Assert.True(publisher.Stop());
        await publisher.Completion;
        Assert.True(publisher.Completion.IsCompletedSuccessfully);
        Assert.True(publisher.HandlesReleased);
    }

    [Fact]
    public async Task DiagnosticsPublisherEventuallyReleasesHandlesAfterTimedOutStop()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var publisher = new LatestAudioDiagnosticsPublisher(_ =>
        {
            entered.Set();
            release.Wait();
        }, TimeSpan.FromMilliseconds(20));

        publisher.Start();
        publisher.PublishLatest(CreateDiagnosticsEventArgs());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(publisher.Stop());
        Assert.False(publisher.HandlesReleased);

        release.Set();
        await publisher.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(publisher.HandlesReleased);
        Assert.True(publisher.Stop());
    }

    [Fact]
    public async Task SlowControlSubscriberCannotDelayProducerAndStopWaitsForIt()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var publisher = new AudioControlEventPublisher(_ =>
        {
            entered.Set();
            release.Wait();
        }, _ => { }, stopTimeout: TimeSpan.FromMilliseconds(20));

        publisher.Start();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Starting, AudioMonitorState.Monitoring)));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.False(publisher.Stop());
        Assert.False(publisher.Completion.IsCompleted);

        release.Set();
        await publisher.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(publisher.HandlesReleased);
    }

    [Fact]
    public async Task ControlPublisherContainsThrowingSubscriberAndDeliversToLaterSubscriber()
    {
        var delivered = 0;
        EventHandler<AudioMonitorStateChangedEventArgs> handlers = (_, _) => throw new InvalidOperationException("observer");
        handlers += (_, _) => delivered++;
        using var publisher = new AudioControlEventPublisher(
            value => SafeAudioEventDispatch.Publish(handlers, this, value, NullApplicationLogger.Instance, "test"),
            _ => { });

        publisher.Start();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.True(publisher.Stop());
        await publisher.Completion;
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task ControlPublisherPreservesStateOrder()
    {
        var delivered = new List<AudioMonitorState>();
        using var publisher = new AudioControlEventPublisher(value => delivered.Add(value.CurrentState), _ => { });
        publisher.Start();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Starting, AudioMonitorState.Monitoring)));
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Monitoring, AudioMonitorState.Muted)));
        Assert.True(publisher.Stop());
        await publisher.Completion;

        Assert.Equal([AudioMonitorState.Starting, AudioMonitorState.Monitoring, AudioMonitorState.Muted], delivered);
    }

    [Fact]
    public async Task ControlPublisherCoalescesRedundantPendingState()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var delivered = new List<AudioMonitorState>();
        using var publisher = new AudioControlEventPublisher(value =>
        {
            delivered.Add(value.CurrentState);
            if (value.CurrentState == AudioMonitorState.Starting)
            {
                entered.Set();
                release.Wait();
            }
        }, _ => { });

        publisher.Start();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Starting, AudioMonitorState.Monitoring)));
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Starting, AudioMonitorState.Monitoring)));
        release.Set();
        Assert.True(publisher.Stop());
        await publisher.Completion;

        Assert.Equal([AudioMonitorState.Starting, AudioMonitorState.Monitoring], delivered);
    }

    [Fact]
    public async Task FailureIsDeliveredUnderStateQueuePressure()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var failures = 0;
        using var publisher = new AudioControlEventPublisher(value =>
        {
            if (value.CurrentState == AudioMonitorState.Starting)
            {
                entered.Set();
                release.Wait();
            }
        }, _ => failures++, capacity: 2);

        publisher.Start();
        Assert.True(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        _ = publisher.PublishState(StateChange(AudioMonitorState.Starting, AudioMonitorState.Monitoring));
        _ = publisher.PublishState(StateChange(AudioMonitorState.Monitoring, AudioMonitorState.Muted));
        _ = publisher.PublishState(StateChange(AudioMonitorState.Muted, AudioMonitorState.Stopping));
        Assert.True(publisher.PublishFailure(FailureEvent()));

        release.Set();
        Assert.True(publisher.Stop());
        await publisher.Completion;
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task NoControlEventIsDeliveredAfterCompletion()
    {
        var delivered = 0;
        using var publisher = new AudioControlEventPublisher(_ => delivered++, _ => delivered++);
        publisher.Start();
        Assert.True(publisher.Stop());
        await publisher.Completion;

        Assert.False(publisher.PublishState(StateChange(AudioMonitorState.Off, AudioMonitorState.Starting)));
        Assert.False(publisher.PublishFailure(FailureEvent()));
        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task RepeatedStopAndDisposeRemainSafeAfterControllerSettlement()
    {
        using var controller = new AudioQueueRateController(stop => stop.WaitOne(), TimeSpan.FromSeconds(1));
        Assert.True(controller.Start());
        Assert.True(controller.Stop());
        Assert.True(controller.Stop());
        controller.Dispose();
        controller.Dispose();
        await controller.Completion;
    }

    [Fact]
    public async Task UnexpectedControllerWorkerFailureStillSignalsCompletionAndStopsSafely()
    {
        using var controller = new AudioQueueRateController(
            _ => throw new InvalidOperationException("worker"),
            TimeSpan.FromSeconds(1));

        _ = controller.Start();
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(controller.Completion.IsCompletedSuccessfully);
        Assert.True(controller.Stop());
    }

    private static AudioMonitorDiagnosticsEventArgs CreateDiagnosticsEventArgs()
    {
        var id = Guid.NewGuid();
        var format = AudioStreamFormat.CreateIeeeFloat(48_000, 2);
        return new AudioMonitorDiagnosticsEventArgs(id, new AudioMonitorDiagnostics(
            id, "Input", "Output", true, "48 kHz", "48 kHz", format,
            AudioMonitorInitializationPath.ClassicSharedFallback,
            480, 480, 960, 960, 960, 960,
            1, 1, 0, 0, 0, 0, 0, 0,
            100, false, null, null, null));
    }

    private static AudioMonitorStateChangedEventArgs StateChange(AudioMonitorState previous, AudioMonitorState current) =>
        new(Guid.NewGuid(), previous, current);

    private static AudioMonitorFailureEventArgs FailureEvent() => new(
        Guid.NewGuid(),
        new AudioMonitorFailure(AudioMonitorFailureCategory.AudioProcessingFailure, "Audio stopped."));

    private static void WaitUntil(Func<bool> predicate)
    {
        Assert.True(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(1)));
    }

    private sealed class ControlledControllerSession : IAudioMonitorSession
    {
        private readonly AudioQueueRateController controller;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlledControllerSession(WaitHandle release)
        {
            controller = new AudioQueueRateController(
                stop => { _ = stop.WaitOne(); _ = release.WaitOne(); },
                TimeSpan.FromMilliseconds(20));
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public AudioMonitorState State { get; private set; } = AudioMonitorState.Off;
        public Task Completion => completion.Task;
        public bool OwnershipReleased { get; private set; }
        public event EventHandler<AudioMonitorStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<AudioMonitorDiagnosticsEventArgs>? DiagnosticsUpdated { add { } remove { } }
        public event EventHandler<AudioMonitorFailureEventArgs>? MonitoringFailed { add { } remove { } }

        public Task<AudioMonitorStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Assert.True(controller.Start());
            State = AudioMonitorState.Monitoring;
            return Task.FromResult(AudioMonitorStartResult.Started(SessionId));
        }

        public Task<AudioMonitorStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (controller.Stop())
            {
                OwnershipReleased = true;
                State = AudioMonitorState.Off;
                completion.TrySetResult();
                return Task.FromResult(AudioMonitorStopResult.Stopped);
            }

            _ = CompleteEventuallyAsync();
            return Task.FromResult(AudioMonitorStopResult.Timeout(new AudioMonitorFailure(
                AudioMonitorFailureCategory.StopTimeout,
                "Audio monitoring did not stop within its bound.")));
        }

        public void SetVolume(double volumePercent) => _ = volumePercent;
        public void SetMuted(bool muted) => _ = muted;
        public async ValueTask DisposeAsync() => _ = await StopAsync();

        private async Task CompleteEventuallyAsync()
        {
            await controller.Completion;
            OwnershipReleased = true;
            State = AudioMonitorState.Off;
            completion.TrySetResult();
        }
    }
}

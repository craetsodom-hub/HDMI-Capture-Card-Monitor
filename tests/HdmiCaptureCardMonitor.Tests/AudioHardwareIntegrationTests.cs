using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace HdmiCaptureCardMonitor.Tests;

[Trait("Category", "Hardware")]
public sealed class AudioHardwareIntegrationTests
{
    private readonly ITestOutputHelper output;

    public AudioHardwareIntegrationTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task ActiveCoreAudioEndpointsEnumerate()
    {
        using var discovery = new CoreAudioEndpointDiscoveryService(NullApplicationLogger.Instance);
        var result = await discovery.EnumerateActiveEndpointsAsync();
        Assert.True(result.IsSuccess, result.Failure?.CustomerMessage);
        Assert.NotEmpty(result.CaptureEndpoints);
        Assert.NotEmpty(result.RenderEndpoints);
        output.WriteLine("Capture endpoints: {0}", string.Join(", ", result.CaptureEndpoints.Select(endpoint => endpoint.DisplayName)));
        output.WriteLine("Render endpoints: {0}", string.Join(", ", result.RenderEndpoints.Select(endpoint => endpoint.DisplayName)));
        output.WriteLine("Default render: {0}", result.DefaultRenderEndpoint?.DisplayName ?? "Unavailable");
        Assert.All(result.CaptureEndpoints, endpoint => Assert.DoesNotContain(endpoint.Id, endpoint.DisplayName, StringComparison.Ordinal));
        Assert.All(result.RenderEndpoints, endpoint => Assert.DoesNotContain(endpoint.Id, endpoint.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MutedSharedModeMonitoringRemainsBoundedAndReleasesEndpoints()
    {
        using var discovery = new CoreAudioEndpointDiscoveryService(NullApplicationLogger.Instance);
        var endpoints = await discovery.EnumerateActiveEndpointsAsync();
        Assert.True(endpoints.IsSuccess, endpoints.Failure?.CustomerMessage);
        var capture = endpoints.CaptureEndpoints[0];
        var preferAudioClient3 = !string.Equals(Environment.GetEnvironmentVariable("HDMI_AUDIO_FORCE_CLASSIC"), "1", StringComparison.Ordinal);
        var targetPeriods = int.TryParse(Environment.GetEnvironmentVariable("HDMI_AUDIO_QUEUE_TARGET_PERIODS"), out var configuredTarget)
            ? Math.Clamp(configuredTarget, 2, 3)
            : 2;
        using var service = new WasapiAudioMonitorService(
            NullApplicationLogger.Instance, preferAudioClient3, targetPeriods);
        var diagnostics = new ConcurrentQueue<(TimeSpan Elapsed, AudioMonitorDiagnostics Value)>();
        var clock = Stopwatch.StartNew();
        service.DiagnosticsUpdated += (_, e) => diagnostics.Enqueue((clock.Elapsed, e.Diagnostics));
        var start = await service.StartAsync(new AudioMonitorStartRequest(capture, null, 0, initiallyMuted: true));
        Assert.True(start.IsSuccess, start.Failure?.CustomerMessage);
        service.SetMuted(false);
        service.SetMuted(true);
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("HDMI_AUDIO_HARDWARE_SECONDS"), out var configured)
            ? Math.Clamp(configured, 2, 900)
            : 10;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var stop = await service.StopAsync();
        Assert.True(stop.IsSuccess, stop.Failure?.CustomerMessage);
        Assert.NotEmpty(diagnostics);
        var samples = diagnostics.ToArray();
        var final = samples[^1].Value;
        long previousUnderruns = -1;
        long previousOverruns = -1;
        long previousDrops = -1;
        long previousDiscontinuities = 0;
        var discontinuityObservations = new List<(TimeSpan Elapsed, long Count)>();
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if (sample.Value.DiscontinuityCount > previousDiscontinuities)
                discontinuityObservations.Add((sample.Elapsed, sample.Value.DiscontinuityCount));
            var changed = sample.Value.UnderrunCount != previousUnderruns ||
                sample.Value.OverrunCount != previousOverruns || sample.Value.DroppedFrames != previousDrops ||
                sample.Value.DiscontinuityCount != previousDiscontinuities;
            if (changed || index % 100 == 0)
                output.WriteLine("t={0:0.000}s queue={1} max={2} underruns={3} overruns={4} dropped={5} discontinuities={6} adjustment={7:0.0}ppm",
                    sample.Elapsed.TotalSeconds, sample.Value.CurrentQueueFrames, sample.Value.MaximumQueueFrames,
                    sample.Value.UnderrunCount, sample.Value.OverrunCount, sample.Value.DroppedFrames,
                    sample.Value.DiscontinuityCount, sample.Value.AppliedRateAdjustment);
            previousUnderruns = sample.Value.UnderrunCount;
            previousOverruns = sample.Value.OverrunCount;
            previousDrops = sample.Value.DroppedFrames;
            previousDiscontinuities = sample.Value.DiscontinuityCount;
        }
        var queueMinimum = samples.Min(sample => sample.Value.CurrentQueueFrames);
        var queueMaximum = samples.Max(sample => sample.Value.CurrentQueueFrames);
        var adjustments = samples
            .Where(sample => sample.Value.AppliedRateAdjustment.HasValue)
            .Select(sample => sample.Value.AppliedRateAdjustment!.Value)
            .ToArray();
        var adjustmentMinimum = adjustments.Length == 0 ? (double?)null : adjustments.Min();
        var adjustmentMaximum = adjustments.Length == 0 ? (double?)null : adjustments.Max();
        var requestedAdjustments = samples
            .Where(sample => sample.Value.RequestedRateAdjustment.HasValue)
            .Select(sample => sample.Value.RequestedRateAdjustment!.Value)
            .ToArray();
        var requestedMinimum = requestedAdjustments.Length == 0 ? (double?)null : requestedAdjustments.Min();
        var requestedMaximum = requestedAdjustments.Length == 0 ? (double?)null : requestedAdjustments.Max();
        output.WriteLine("Observed queue range={0}..{1} frames; rate-adjustment range={2:0.0}..{3:0.0} ppm",
            queueMinimum, queueMaximum, adjustmentMinimum, adjustmentMaximum);
        output.WriteLine("Target={0} periods/{1} frames; average queue={2:0.0} frames; requested range={3:0.0}..{4:0.0} ppm; saturation={5} ms; direction changes={6}",
            targetPeriods, final.TargetQueueFrames, final.AverageQueueFrames,
            requestedMinimum, requestedMaximum, final.RateAdjustmentSaturationMilliseconds,
            final.RateAdjustmentDirectionChangeCount);
        output.WriteLine("Discontinuity observations: {0}", discontinuityObservations.Count == 0
            ? "none"
            : string.Join(", ", discontinuityObservations.Select(observation =>
                $"t={observation.Elapsed.TotalSeconds:0.000}s count={observation.Count}")));
        foreach (var observation in final.DiscontinuityTimeline ?? [])
            output.WriteLine("Discontinuity detail: t={0:0.000}s phase={1} frames={2} deviceDelta={3} qpcDelta={4} queue={5}->{6} requested={7:0.0}ppm applied={8:0.0}ppm underrunFollowed={9} overrunFollowed={10}",
                observation.MonotonicTime.TotalSeconds, observation.Phase, observation.PacketFrameCount,
                observation.DevicePositionDelta?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                observation.QpcPositionDelta?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                observation.QueueBeforeFrames, observation.QueueAfterFrames,
                observation.RequestedRateAdjustmentPpm, observation.AppliedRateAdjustmentPpm,
                observation.UnderrunFollowed, observation.OverrunFollowed);
        output.WriteLine(
            "Path={0}; format={1}; periods={2}/{3}; buffers={4}/{5}; queue={6} max={7}; captured={8}; rendered={9}; silent={10}; underruns={11}; overruns={12}; dropped={13}; discontinuities={14}; timestampErrors={15}; adjustment={16:0}ppm",
            final.InitializationPath, final.CommonFormat, final.CapturePeriodFrames, final.RenderPeriodFrames,
            final.CaptureBufferFrames, final.RenderBufferFrames, final.CurrentQueueFrames, final.MaximumQueueFrames,
            final.FramesCaptured, final.FramesRendered, final.SilentFrames, final.UnderrunCount,
            final.OverrunCount, final.DroppedFrames, final.DiscontinuityCount, final.TimestampErrorCount,
            final.AppliedRateAdjustment);
        Assert.True(final.FramesCaptured > 0);
        Assert.True(final.FramesRendered > 0);
        Assert.InRange(final.CurrentQueueFrames, 0, final.MaximumQueueFrames);
        Assert.Equal(0, final.UnderrunCount);
        Assert.Equal(0, final.OverrunCount);
        Assert.Equal(0, final.DroppedFrames);
        Assert.Equal(0, final.CurrentVolumePercent);
        Assert.True(final.IsMuted);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task TwentyMutedStartStopCyclesCompleteAndReleaseEndpoints()
    {
        using var discovery = new CoreAudioEndpointDiscoveryService(NullApplicationLogger.Instance);
        var endpoints = await discovery.EnumerateActiveEndpointsAsync();
        Assert.True(endpoints.IsSuccess, endpoints.Failure?.CustomerMessage);
        var capture = endpoints.CaptureEndpoints[0];
        using var service = new WasapiAudioMonitorService(NullApplicationLogger.Instance);
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var start = await service.StartAsync(new AudioMonitorStartRequest(capture, null, 0, initiallyMuted: true));
            Assert.True(start.IsSuccess, $"Cycle {cycle + 1}: {start.Failure?.CustomerMessage}");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var stop = await service.StopAsync();
            Assert.True(stop.IsSuccess, $"Cycle {cycle + 1}: {stop.Failure?.CustomerMessage}");
            Assert.False(service.IsActive);
        }
    }
}

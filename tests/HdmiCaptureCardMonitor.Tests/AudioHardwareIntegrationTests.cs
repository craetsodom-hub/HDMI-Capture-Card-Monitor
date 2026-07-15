using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        using var service = new WasapiAudioMonitorService(NullApplicationLogger.Instance, preferAudioClient3);
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
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var changed = sample.Value.UnderrunCount != previousUnderruns ||
                sample.Value.OverrunCount != previousOverruns || sample.Value.DroppedFrames != previousDrops;
            if (changed || index % 20 == 0)
                output.WriteLine("t={0:0.0}s queue={1} max={2} underruns={3} overruns={4} dropped={5} adjustment={6:0}ppm",
                    sample.Elapsed.TotalSeconds, sample.Value.CurrentQueueFrames, sample.Value.MaximumQueueFrames,
                    sample.Value.UnderrunCount, sample.Value.OverrunCount, sample.Value.DroppedFrames,
                    sample.Value.AppliedRateAdjustment);
            previousUnderruns = sample.Value.UnderrunCount;
            previousOverruns = sample.Value.OverrunCount;
            previousDrops = sample.Value.DroppedFrames;
        }
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

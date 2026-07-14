using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Diagnostics;

internal sealed class PreviewDiagnosticsTracker(string deviceDisplayName, string requestedFormat)
{
    private const int TimingCapacity = 256;
    private const int FpsCapacity = 120;
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromMilliseconds(500);
    private readonly object syncRoot = new();
    private readonly double[] processingMilliseconds = new double[TimingCapacity];
    private readonly DateTimeOffset[] frameTimes = new DateTimeOffset[FpsCapacity];
    private int timingCount;
    private int timingNext;
    private int frameTimeCount;
    private int frameTimeNext;
    private DateTimeOffset lastPublication;
    private string actualNativeFormat = string.Empty;
    private string outputSubtype = string.Empty;
    private PreviewDriverType driverType;
    private long framesReceived;
    private long framesRendered;
    private long nullSamples;
    private long streamTicks;
    private long presentationFailures;
    private long? lastSampleTimestamp;
    private DateTimeOffset? lastSuccessfulFrameTime;
    private int consecutiveReadFailures;
    private PreviewFailureCategory? lastFailureCategory;

    public static int TimingSampleCapacity => TimingCapacity;

    public void SetNegotiation(string actualFormat, string negotiatedSubtype, PreviewDriverType driver)
    {
        lock (syncRoot)
        {
            actualNativeFormat = actualFormat;
            outputSubtype = negotiatedSubtype;
            driverType = driver;
        }
    }

    public void RecordReceived(long timestamp)
    {
        lock (syncRoot)
        {
            framesReceived++;
            lastSampleTimestamp = timestamp;
            consecutiveReadFailures = 0;
        }
    }

    public void RecordRendered(TimeSpan processingTime, DateTimeOffset presentedAt)
    {
        lock (syncRoot)
        {
            framesRendered++;
            lastSuccessfulFrameTime = presentedAt;
            processingMilliseconds[timingNext] = Math.Max(0, processingTime.TotalMilliseconds);
            timingNext = (timingNext + 1) % TimingCapacity;
            timingCount = Math.Min(timingCount + 1, TimingCapacity);
            frameTimes[frameTimeNext] = presentedAt;
            frameTimeNext = (frameTimeNext + 1) % FpsCapacity;
            frameTimeCount = Math.Min(frameTimeCount + 1, FpsCapacity);
        }
    }

    public void RecordNullSample() { lock (syncRoot) nullSamples++; }
    public void RecordStreamTick() { lock (syncRoot) streamTicks++; }
    public void RecordReadFailure() { lock (syncRoot) consecutiveReadFailures++; }
    public void RecordPresentationFailure() { lock (syncRoot) presentationFailures++; }
    public void RecordFailure(PreviewFailureCategory category) { lock (syncRoot) lastFailureCategory = category; }

    public bool TryCreateThrottledSnapshot(DateTimeOffset now, out PreviewDiagnostics diagnostics)
    {
        lock (syncRoot)
        {
            if (lastPublication != default && now - lastPublication < PublicationInterval)
            {
                diagnostics = CreateSnapshotCore(now);
                return false;
            }

            lastPublication = now;
            diagnostics = CreateSnapshotCore(now);
            return true;
        }
    }

    public PreviewDiagnostics CreateSnapshot(DateTimeOffset now)
    {
        lock (syncRoot) return CreateSnapshotCore(now);
    }

    private PreviewDiagnostics CreateSnapshotCore(DateTimeOffset now)
    {
        var timings = new double[timingCount];
        for (var index = 0; index < timingCount; index++) timings[index] = processingMilliseconds[index];
        Array.Sort(timings);
        var average = timings.Length == 0 ? 0 : timings.Average();
        var p95Index = timings.Length == 0 ? 0 : (int)Math.Ceiling(timings.Length * 0.95) - 1;
        var p95 = timings.Length == 0 ? 0 : timings[Math.Clamp(p95Index, 0, timings.Length - 1)];

        var fps = 0d;
        if (frameTimeCount > 1)
        {
            var oldestIndex = frameTimeCount == FpsCapacity ? frameTimeNext : 0;
            var oldest = frameTimes[oldestIndex];
            var newestIndex = (frameTimeNext - 1 + FpsCapacity) % FpsCapacity;
            var elapsed = frameTimes[newestIndex] - oldest;
            if (elapsed.TotalSeconds > 0) fps = (frameTimeCount - 1) / elapsed.TotalSeconds;
        }

        _ = now;
        return new PreviewDiagnostics(
            deviceDisplayName,
            requestedFormat,
            actualNativeFormat,
            outputSubtype,
            driverType,
            framesReceived,
            framesRendered,
            nullSamples,
            streamTicks,
            presentationFailures,
            fps,
            average,
            p95,
            lastSampleTimestamp,
            lastSuccessfulFrameTime,
            consecutiveReadFailures,
            lastFailureCategory);
    }
}

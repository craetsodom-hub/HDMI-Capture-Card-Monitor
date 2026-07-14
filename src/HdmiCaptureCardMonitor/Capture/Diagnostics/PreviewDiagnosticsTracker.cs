using HdmiCaptureCardMonitor.Models;

namespace HdmiCaptureCardMonitor.Capture.Diagnostics;

internal sealed class PreviewDiagnosticsTracker(string deviceDisplayName, string requestedFormat)
{
    private const int TimingCapacity = 256;
    private const int CadenceCapacity = 120;
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromMilliseconds(500);
    private readonly object syncRoot = new();
    private readonly double[] processingMilliseconds = new double[TimingCapacity];
    private readonly double[] sampleReturnToPresentMilliseconds = new double[TimingCapacity];
    private readonly double[] receivedTimesSeconds = new double[CadenceCapacity];
    private readonly double[] renderedTimesSeconds = new double[CadenceCapacity];
    private readonly long[] sampleTimestamps = new long[CadenceCapacity];
    private int timingCount;
    private int timingNext;
    private int receivedTimeCount;
    private int receivedTimeNext;
    private int renderedTimeCount;
    private int renderedTimeNext;
    private int sampleTimestampCount;
    private int sampleTimestampNext;
    private DateTimeOffset lastPublication;
    private string actualNativeFormat = string.Empty;
    private string outputSubtype = string.Empty;
    private PreviewDriverType driverType;
    private uint actualOutputWidth;
    private uint actualOutputHeight;
    private uint actualOutputFrameRateNumerator;
    private uint actualOutputFrameRateDenominator;
    private VideoInterlaceMode actualOutputInterlaceMode;
    private long framesReceived;
    private long framesRendered;
    private long nullSamples;
    private long streamTicks;
    private long presentationFailures;
    private long framesSkippedForUnavailableSurface;
    private long presentWasStillDrawingCount;
    private long? lastSampleTimestamp;
    private DateTimeOffset? lastSuccessfulFrameTime;
    private int consecutiveReadFailures;
    private PreviewFailureCategory? lastFailureCategory;

    public static int TimingSampleCapacity => TimingCapacity;

    public void SetNegotiation(
        string actualFormat,
        string negotiatedSubtype,
        PreviewDriverType driver,
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator,
        VideoInterlaceMode interlaceMode)
    {
        lock (syncRoot)
        {
            actualNativeFormat = actualFormat;
            outputSubtype = negotiatedSubtype;
            driverType = driver;
            actualOutputWidth = width;
            actualOutputHeight = height;
            actualOutputFrameRateNumerator = frameRateNumerator;
            actualOutputFrameRateDenominator = frameRateDenominator;
            actualOutputInterlaceMode = interlaceMode;
        }
    }

    public void RecordReceived(long timestamp, TimeSpan arrivalElapsed)
    {
        lock (syncRoot)
        {
            framesReceived++;
            lastSampleTimestamp = timestamp;
            consecutiveReadFailures = 0;
            receivedTimesSeconds[receivedTimeNext] = arrivalElapsed.TotalSeconds;
            receivedTimeNext = (receivedTimeNext + 1) % CadenceCapacity;
            receivedTimeCount = Math.Min(receivedTimeCount + 1, CadenceCapacity);
            sampleTimestamps[sampleTimestampNext] = timestamp;
            sampleTimestampNext = (sampleTimestampNext + 1) % CadenceCapacity;
            sampleTimestampCount = Math.Min(sampleTimestampCount + 1, CadenceCapacity);
        }
    }

    public void RecordRendered(
        TimeSpan processingTime,
        TimeSpan sampleReturnToPresentTime,
        TimeSpan presentedElapsed,
        DateTimeOffset presentedAt)
    {
        lock (syncRoot)
        {
            framesRendered++;
            lastSuccessfulFrameTime = presentedAt;
            processingMilliseconds[timingNext] = Math.Max(0, processingTime.TotalMilliseconds);
            sampleReturnToPresentMilliseconds[timingNext] = Math.Max(0, sampleReturnToPresentTime.TotalMilliseconds);
            timingNext = (timingNext + 1) % TimingCapacity;
            timingCount = Math.Min(timingCount + 1, TimingCapacity);
            renderedTimesSeconds[renderedTimeNext] = presentedElapsed.TotalSeconds;
            renderedTimeNext = (renderedTimeNext + 1) % CadenceCapacity;
            renderedTimeCount = Math.Min(renderedTimeCount + 1, CadenceCapacity);
        }
    }

    public void RecordNullSample() { lock (syncRoot) nullSamples++; }
    public void RecordStreamTick() { lock (syncRoot) streamTicks++; }
    public void RecordReadFailure() { lock (syncRoot) consecutiveReadFailures++; }
    public void RecordPresentationFailure() { lock (syncRoot) presentationFailures++; }
    public void RecordSurfaceUnavailableSkip() { lock (syncRoot) framesSkippedForUnavailableSurface++; }
    public void RecordPresentWasStillDrawing() { lock (syncRoot) presentWasStillDrawingCount++; }
    public void RecordFailure(PreviewFailureCategory category) { lock (syncRoot) lastFailureCategory = category; }

    public bool TryCreateThrottledSnapshot(DateTimeOffset now, out PreviewDiagnostics diagnostics)
    {
        lock (syncRoot)
        {
            if (lastPublication != default && now - lastPublication < PublicationInterval)
            {
                diagnostics = CreateSnapshotCore();
                return false;
            }

            lastPublication = now;
            diagnostics = CreateSnapshotCore();
            return true;
        }
    }

    public PreviewDiagnostics CreateSnapshot(DateTimeOffset now)
    {
        _ = now;
        lock (syncRoot) return CreateSnapshotCore();
    }

    private PreviewDiagnostics CreateSnapshotCore()
    {
        var (averageProcessing, p95Processing) = CalculateTiming(processingMilliseconds, timingCount);
        var (averageReturnToPresent, p95ReturnToPresent) = CalculateTiming(sampleReturnToPresentMilliseconds, timingCount);
        var receivedFps = CalculateCadence(receivedTimesSeconds, receivedTimeCount, receivedTimeNext, CadenceCapacity, 1d);
        var renderedFps = CalculateCadence(renderedTimesSeconds, renderedTimeCount, renderedTimeNext, CadenceCapacity, 1d);
        var timestampFps = CalculateCadence(sampleTimestamps, sampleTimestampCount, sampleTimestampNext, CadenceCapacity, 10_000_000d);

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
            renderedFps,
            averageProcessing,
            p95Processing,
            lastSampleTimestamp,
            lastSuccessfulFrameTime,
            consecutiveReadFailures,
            lastFailureCategory,
            actualOutputWidth,
            actualOutputHeight,
            actualOutputFrameRateNumerator,
            actualOutputFrameRateDenominator,
            actualOutputInterlaceMode,
            receivedFps,
            timestampFps,
            averageReturnToPresent,
            p95ReturnToPresent,
            framesSkippedForUnavailableSurface,
            presentWasStillDrawingCount);
    }

    private static (double Average, double P95) CalculateTiming(double[] source, int count)
    {
        if (count == 0) return (0, 0);
        var values = new double[count];
        Array.Copy(source, values, count);
        Array.Sort(values);
        var p95Index = Math.Clamp((int)Math.Ceiling(values.Length * 0.95) - 1, 0, values.Length - 1);
        return (values.Average(), values[p95Index]);
    }

    private static double CalculateCadence(double[] values, int count, int next, int capacity, double unitsPerSecond)
    {
        if (count <= 1) return 0;
        var oldest = values[count == capacity ? next : 0];
        var newest = values[(next - 1 + capacity) % capacity];
        var elapsedSeconds = (newest - oldest) / unitsPerSecond;
        return elapsedSeconds > 0 ? (count - 1) / elapsedSeconds : 0;
    }

    private static double CalculateCadence(long[] values, int count, int next, int capacity, double unitsPerSecond)
    {
        if (count <= 1) return 0;
        var oldest = values[count == capacity ? next : 0];
        var newest = values[(next - 1 + capacity) % capacity];
        var elapsedSeconds = (newest - oldest) / unitsPerSecond;
        return elapsedSeconds > 0 ? (count - 1) / elapsedSeconds : 0;
    }
}

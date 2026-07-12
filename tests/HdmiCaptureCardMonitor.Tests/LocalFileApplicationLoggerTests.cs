using HdmiCaptureCardMonitor.Infrastructure;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class LocalFileApplicationLoggerTests
{
    [Fact]
    public void CreatesDistinctFilesForInstancesWithTheSameTimestampAndProcessId()
    {
        using var directory = new TemporaryDirectory();
        var timestamp = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

        using var first = new LocalFileApplicationLogger(directory.Path, utcNow: () => timestamp, processId: () => 1234);
        using var second = new LocalFileApplicationLogger(directory.Path, utcNow: () => timestamp, processId: () => 1234);

        Assert.Equal(2, Directory.GetFiles(directory.Path, "app-*.log").Length);
    }

    [Fact]
    public void RetentionKeepsTheConfiguredNumberOfApplicationLogsAndLeavesOtherFilesUntouched()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 4; index++)
        {
            var path = Path.Combine(directory.Path, $"app-20260712-120000-1-{index}.log");
            File.WriteAllText(path, "test");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + index));
        }

        var unrelatedFile = Path.Combine(directory.Path, "keep-me.txt");
        File.WriteAllText(unrelatedFile, "unrelated");

        using var logger = new LocalFileApplicationLogger(directory.Path, retentionCount: 2);

        Assert.Equal(2, Directory.GetFiles(directory.Path, "app-*.log").Length);
        Assert.True(File.Exists(unrelatedFile));
    }

    [Fact]
    public void FactoryProvidesAnExplicitNonFatalFallbackWhenTheLogDirectoryCannotBeCreated()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(filePath, "block directory creation");

        var result = ApplicationLoggerFactory.Create(filePath);

        Assert.False(result.IsFileLoggingAvailable);
        Assert.NotNull(result.StartupNotice);
        Assert.Same(NullApplicationLogger.Instance, result.Logger);
        result.Logger.Warning("This no-op call must be safe.");
    }

    [Fact]
    public void DoesNotWriteAfterDisposalAndDisposalIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var logger = new LocalFileApplicationLogger(directory.Path);
        logger.Information("before disposal");
        logger.Dispose();
        logger.Information("after disposal");
        logger.Dispose();

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Path, "app-*.log")));
        Assert.Contains("before disposal", content, StringComparison.Ordinal);
        Assert.DoesNotContain("after disposal", content, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HdmiCaptureCardMonitorTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

namespace HdmiCaptureCardMonitor.Models;

public sealed record DiscoveryResult<T>(T? Value, DiscoveryFailure? Failure, bool IsCancelled)
{
    public bool IsSuccess => Failure is null && !IsCancelled;
}

public static class DiscoveryResults
{
    public static DiscoveryResult<T> Success<T>(T value) => new(value, null, false);

    public static DiscoveryResult<T> Cancelled<T>() => new(default, null, true);

    public static DiscoveryResult<T> Failed<T>(DiscoveryFailure failure) => new(default, failure, false);
}

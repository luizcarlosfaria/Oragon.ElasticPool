using Microsoft.Extensions.Logging;

namespace Oragon.ElasticPool.Core.Telemetry;

internal static partial class PoolDiagnosticsLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Pool item leaked (consumer forgot to dispose). Returning defensively to pool '{PoolName}'.")]
    public static partial void ItemLeaked(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Factory hook threw for pool '{PoolName}'. Counter rolled back.")]
    public static partial void FactoryFailed(this ILogger logger, string poolName, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "BeforeUse hook reported Unhealthy for pool '{PoolName}'. Invoking failure policy.")]
    public static partial void BeforeUseUnhealthy(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Release hook threw during pool dispose for pool '{PoolName}'. Continuing drain.")]
    public static partial void ReleaseHookFailedDuringDispose(this ILogger logger, string poolName, Exception exception);

    // ---------------- Phase 2 entries (1005-1010, 1099) ----------------

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Pool '{PoolName}' grew {Old}->{New} (tripWaiters={TripWaiters}, tripUtilization={TripUtilization}, tripP95={TripP95}).")]
    public static partial void Grew(this ILogger logger, string poolName, int old, int @new, bool tripWaiters, bool tripUtilization, bool tripP95);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "Pool '{PoolName}' shrunk {OldTotal}->{NewTotal}.")]
    public static partial void Shrunk(this ILogger logger, string poolName, int oldTotal, int newTotal);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug,
        Message = "Sweep tick started for pool '{PoolName}' (interval={IntervalSeconds}s).")]
    public static partial void SweepStarted(this ILogger logger, string poolName, double intervalSeconds);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug,
        Message = "Sweep tick completed for pool '{PoolName}' in {DurationMs}ms (checked={Checked}, unhealthy={Unhealthy}, shrunk={Shrunk}).")]
    public static partial void SweepCompleted(this ILogger logger, string poolName, double durationMs, int @checked, int unhealthy, int shrunk);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
        Message = "Sweep failure backoff engaged for pool '{PoolName}': interval {OldSec}s -> {NewSec}s after {ConsecutiveWindows} consecutive failure windows.")]
    public static partial void SweepFailureBackoff(this ILogger logger, string poolName, double oldSec, double newSec, int consecutiveWindows);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning,
        Message = "Check hook reported Unhealthy for pool '{PoolName}' (reason={Reason}).")]
    public static partial void CheckUnhealthy(this ILogger logger, string poolName, string reason);

    [LoggerMessage(EventId = 1099, Level = LogLevel.Error,
        Message = "Sweep tick failed catastrophically for pool '{PoolName}'. Loop continues.")]
    public static partial void SweepFailed(this ILogger logger, string poolName, Exception exception);
}

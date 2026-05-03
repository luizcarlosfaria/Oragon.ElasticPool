using Microsoft.Extensions.Logging;

namespace Oragon.AdaptivePool.Core.Telemetry;

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
}

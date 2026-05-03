using Microsoft.Extensions.Logging;

namespace Oragon.AdaptivePool.RabbitMQ.Internals;

/// <summary>
/// Source-generated logger entries for the RabbitMQ adapter. EventId 2001+ is reserved
/// for adapter diagnostics (Core uses 1xxx). Allocation-free dispatch via
/// <c>[LoggerMessage]</c> source generation.
/// </summary>
internal static partial class AdapterDiagnosticsLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "AutomaticRecoveryEnabled was true on the configured ConnectionFactory for pool '{PoolName}'; Oragon.AdaptivePool overrides this to false (the pool owns lifecycle).")]
    public static partial void AutomaticRecoveryOverridden(this ILogger logger, string poolName);
}

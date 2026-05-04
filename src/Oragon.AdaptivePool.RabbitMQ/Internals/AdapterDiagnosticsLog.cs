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

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Debug,
        Message = "Channel pool '{PoolName}': Release hook found no paired shared connection lease for the channel. Connection lease accounting may be lost.")]
    public static partial void UnpairedChannelRelease(this ILogger logger, string poolName);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Channel pool '{PoolName}': IChannel.DisposeAsync threw during Release; cleanup of tracker/pairing/connection-lease will still execute.")]
    public static partial void ChannelDisposeFailed(this ILogger logger, string poolName, Exception exception);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Connection pool '{PoolName}': IConnection.DisposeAsync threw during Release.")]
    public static partial void ConnectionDisposeFailed(this ILogger logger, string poolName, Exception exception);
}

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.ElasticPool.Core.Telemetry;

namespace Oragon.ElasticPool.Core.Benchmarks;

/// <summary>
/// Phase 2 Plan 03 / ROADMAP success criterion 5: verify [LoggerMessage] source-gen entries
/// are allocation-free per call when the underlying logger reports IsEnabled=true. The
/// EnabledNullProvider below ensures the source-gen dispatch fully runs (the IsEnabled=false
/// short-circuit would trivially produce 0 allocations even with naive LogInformation calls).
/// </summary>
[MemoryDiagnoser]
public class PoolDiagnosticsLogBenchmarks
{
    private ILogger _logger = NullLogger.Instance;
    private const string PoolName = "bench-pool";

    [GlobalSetup]
    public void Setup()
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(new EnabledNullProvider()));
        _logger = factory.CreateLogger("bench");
    }

    [Benchmark] public void Grew()
        => _logger.Grew(PoolName, 5, 6, true, false, false);

    [Benchmark] public void Shrunk()
        => _logger.Shrunk(PoolName, 6, 5);

    [Benchmark] public void SweepStarted()
        => _logger.SweepStarted(PoolName, 30.0);

    [Benchmark] public void SweepCompleted()
        => _logger.SweepCompleted(PoolName, 12.5, 5, 0, 1);

    [Benchmark] public void SweepFailureBackoff()
        => _logger.SweepFailureBackoff(PoolName, 30.0, 60.0, 3);

    [Benchmark] public void CheckUnhealthy()
        => _logger.CheckUnhealthy(PoolName, "TimeoutException");

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && (string.Equals(args[0], "elasticity", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(args[0], "--elasticity", StringComparison.OrdinalIgnoreCase)))
        {
            await HeavyResourceElasticityRunner.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(PoolDiagnosticsLogBenchmarks).Assembly).Run(args);
    }
}

/// <summary>
/// Cheapest possible logger that nonetheless runs the full source-gen dispatch path:
/// IsEnabled=true causes the [LoggerMessage] partial method to construct the LogState
/// value-type and call into Log; the formatter is invoked but the result is discarded.
/// This isolates [LoggerMessage] allocation behavior from sink/formatter cost.
/// </summary>
internal sealed class EnabledNullProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new EnabledNullLogger();
    public void Dispose() { }

    private sealed class EnabledNullLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Discard — measure source-gen + state-construction, not formatting.
        }
    }
}

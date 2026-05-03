using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Internals;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

/// <summary>
/// End-to-end backoff progression tests. Drive a Check hook that returns Unhealthy on
/// every invocation and observe the SweepBackoffState curve via the internal probe
/// <c>pool.BackoffState.CurrentInterval</c>.
/// </summary>
public class SweepBackoffIntegrationTests
{
    private static (AdaptivePool<Resource> pool, ServiceProvider sp) Build(
        FakeTimeProvider fake,
        Func<Resource, CancellationToken, ValueTask<PoolState>> check,
        TimeSpan? maxBackoff = null,
        ILoggerProvider? logProvider = null)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            if (logProvider is not null) b.AddProvider(logProvider);
        });
        var sp = services.BuildServiceProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 3, maxSize: 10, initialSize: 3)
            .WithTimeProvider(fake)
            .SweepInterval(TimeSpan.FromSeconds(30))
            .Check((r, ct) => check(r, ct));
        if (maxBackoff is not null) builder = builder.MaxBackoff(maxBackoff.Value);
        return ((AdaptivePool<Resource>)builder.Build(), sp);
    }

    [Fact(Timeout = 30_000)]
    public async Task Backoff_AfterThreeFailureWindows_DoublesInterval()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp) = Build(fake, (r, ct) => ValueTask.FromResult(PoolState.Unhealthy));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            // Tick 1: 3/3 unhealthy → consecutive=1 (no bump)
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            // Tick 2: 3/3 unhealthy → consecutive=2 (no bump)
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            // Tick 3: 3/3 unhealthy → consecutive=3 → interval doubles to 60s
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            pool.BackoffState.CurrentInterval.Should().Be(TimeSpan.FromSeconds(60));
            pool.BackoffState.ConsecutiveFailureWindows.Should().Be(3);
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Backoff_ResetsOnFirstCleanTick()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var unhealthyMode = true;
        var (pool, sp) = Build(fake, (r, ct) =>
            ValueTask.FromResult(unhealthyMode ? PoolState.Unhealthy : PoolState.Healthy));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            for (int i = 0; i < 3; i++)
                await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.BackoffState.CurrentInterval.Should().BeGreaterThan(TimeSpan.FromSeconds(30));

            // Switch to healthy. The sweep loop reads CurrentInterval as its new period —
            // but the previous tick body already requested period bump to 60s, so we must
            // advance by the NEW interval.
            unhealthyMode = false;

            // Note: SinceLastGrowTicks may have caused a shrink to happen in the meantime;
            // shrink would only happen if cooldown elapsed AND items were past IdleTimeout
            // AND > MinSize. Items are still LastReturnedAt=startTime; they may have been
            // marked LastReturnedAt=MinValue by the unhealthy verdicts. But the pool may
            // have shrunk past MinSize=3 — so we can't rely on items remaining.

            // Re-warm: enqueue fresh items by acquiring & releasing.
            // Actually easier: fewer items means fewer Check calls, but that's ok — still
            // a clean window if all 0 are unhealthy. But the state machine treats totalChecked=0
            // as no-op. We need at least one Check call. Verify pool has idle items:
            if (pool.Available == 0)
            {
                // Force an acquire/release to populate idle (factory still works).
                var item = await pool.AcquireAsync();
                await item.DisposeAsync();
            }

            // Advance the new period (60s) to fire the next tick under healthy Check.
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(60));

            pool.BackoffState.CurrentInterval.Should().Be(TimeSpan.FromSeconds(30),
                "first clean tick resets interval to base 30s");
            pool.BackoffState.ConsecutiveFailureWindows.Should().Be(0);
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Backoff_CapsAtMaxBackoff()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp) = Build(fake,
            (r, ct) => ValueTask.FromResult(PoolState.Unhealthy),
            maxBackoff: TimeSpan.FromSeconds(120));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            // Drive enough failure windows to saturate. The interval doubles on each failure
            // window past the third: 30, 30, 30, 60, 120, 240→cap=120, 120, ...
            // After 6 ticks at the failing rate we should be at cap.
            // BUT: timer.Period changes mid-flight, so each Advance must use the CURRENT period.
            for (int tick = 0; tick < 10; tick++)
            {
                var period = pool.BackoffState.CurrentInterval;
                await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, period);
            }

            pool.BackoffState.CurrentInterval.Should().Be(TimeSpan.FromSeconds(120),
                "interval saturates at MaxBackoff");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Backoff_LogsSweepFailureBackoff_OnTransition()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var capturedLogs = new CapturedLogEntries();
        var (pool, sp) = Build(fake,
            (r, ct) => ValueTask.FromResult(PoolState.Unhealthy),
            logProvider: capturedLogs);
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            for (int i = 0; i < 3; i++)
                await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            // EventId 1009 fires on the bump tick.
            capturedLogs.ByEventId(1009).Should().NotBeEmpty(
                "SweepFailureBackoff (1009) must fire when the interval ACTUALLY increases");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }
}

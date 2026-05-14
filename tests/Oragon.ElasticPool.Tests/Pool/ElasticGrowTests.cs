using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.DependencyInjection;
using Oragon.ElasticPool.Internals;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

/// <summary>
/// Composite-signal grow integration tests. Each signal is exercised in isolation by setting
/// the other two thresholds to unreachable values, then a combined-OR test demonstrates
/// independent triggering.
/// </summary>
public class ElasticGrowTests
{
    private static (ElasticPool<Resource> pool, ServiceProvider sp, string poolName) Build(
        Action<ElasticPoolBuilder<Resource>> configure,
        FakeTimeProvider? fake = null)
    {
        var poolName = $"elastic-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b =>
        {
            b.Factory((s, ct) => ValueTask.FromResult(new Resource()));
            if (fake is not null) b.WithTimeProvider(fake);
            configure(b);
        });
        var sp = services.BuildServiceProvider();
        var pool = (ElasticPool<Resource>)sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
        return (pool, sp, poolName);
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_OnWaiterSignalOnly_RaisesPoolSize()
    {
        var (pool, sp, poolName) = Build(b => b
            .WithBounds(minSize: 0, maxSize: 10, initialSize: 0)
            .GrowOnWaiterCount(1)
            .GrowOnUtilizationPercent(1.0) // saturate-only — never trips below 100%
            .GrowOnWaitTimeP95(TimeSpan.FromHours(1)));
        try
        {
            using var growCounter = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(),
                "Oragon.ElasticPool", "pool.grow.count");

            // First acquire: total goes 0→1 via grow path.
            var first = await pool.AcquireAsync();
            pool.CurrentTotal.Should().Be(1);
            // Second acquire is parallel — becomes a waiter, triggering grow on waiterCount=1.
            var secondTask = pool.AcquireAsync().AsTask();
            await Task.Delay(100); // let the waiter park / trigger grow
            var second = await secondTask;

            pool.CurrentTotal.Should().Be(2);
            growCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().BeGreaterThanOrEqualTo(1,
                "at least one grow must increment pool.grow.count");

            await first.DisposeAsync();
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_OnP95SignalOnly_RaisesPoolSize()
    {
        var (pool, sp, poolName) = Build(b => b
            .WithBounds(minSize: 1, maxSize: 10, initialSize: 1) // at least 1 idle so the next path doesn't go via waiter
            .GrowOnWaiterCount(10)                       // == MaxSize, effectively unreachable for 1 caller
            .GrowOnUtilizationPercent(1.0)               // unreachable
            .GrowOnWaitTimeP95(TimeSpan.FromMilliseconds(50)));
        try
        {
            await pool.ReadyAsync();
            // Force the wait histogram to a high p95 directly via the internal probe.
            for (int i = 0; i < 100; i++)
            {
                pool.WaitHistogram.Record(TimeSpan.FromMilliseconds(150));
            }

            using var growCounter = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(),
                "Oragon.ElasticPool", "pool.grow.count");

            // Reserve the only idle item to force the slow path on the next acquire.
            var holder = await pool.AcquireAsync();

            // Next acquire goes slow path. It will be ineligible by waiter (idle was 0; the second
            // acquire counts itself as parked → 1, but we set GrowOnWaiterCount=int.MaxValue).
            // P95 trips → grow.
            var growTask = pool.AcquireAsync().AsTask();
            var grew = await growTask.WaitAsync(TimeSpan.FromSeconds(5));
            pool.CurrentTotal.Should().BeGreaterThanOrEqualTo(2);

            await holder.DisposeAsync();
            await grew.DisposeAsync();

            growCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_OrSemantics_AnyOneSignalSuffices()
    {
        // We've already verified waiter-only and p95-only above. This test exercises BOTH at once
        // and asserts the GrowDecision tag flags reflect the OR (no short-circuit).
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp, poolName) = Build(b => b
            .WithBounds(minSize: 0, maxSize: 10, initialSize: 0)
            .GrowOnWaiterCount(1)
            .GrowOnUtilizationPercent(1.0)
            .GrowOnWaitTimeP95(TimeSpan.FromMilliseconds(50)),
            fake);
        try
        {
            // Pre-load wait histogram so p95 also trips.
            for (int i = 0; i < 100; i++)
            {
                pool.WaitHistogram.Record(TimeSpan.FromMilliseconds(150));
            }

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            var item = await pool.AcquireAsync();

            // The first slow-path entry must trip BOTH byWaiters and byP95.
            var growSpan = captured.ByNameAndPool("Pool.Grow", poolName).Single();
            growSpan.GetTagItem("grow.tripped_by_waiters").Should().Be(true);
            growSpan.GetTagItem("grow.tripped_by_p95").Should().Be(true);

            await item.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_RespectsMaxSize_DoesNotExceedTotal()
    {
        var (pool, sp, poolName) = Build(b => b
            .WithBounds(minSize: 0, maxSize: 5, initialSize: 0)
            .GrowOnWaiterCount(1));
        try
        {
            // Saturate the pool: 5 concurrent acquires holding their items. Use deterministic
            // signaling so we know exactly when all 5 have acquired (no polling/race window).
            using var holders = new SemaphoreSlim(0, 5);
            using var allAcquired = new CountdownEvent(5);
            var holdTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
            {
                var item = await pool.AcquireAsync();
                allAcquired.Signal();
                await holders.WaitAsync();
                await item.DisposeAsync();
            })).ToArray();

            // Wait deterministically until all 5 acquires have completed (no time-based polling).
            // 30s ceiling tolerates extreme load on resource-constrained agents while still
            // bounding the test duration to fail fast on real bugs.
            allAcquired.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue(
                "all 5 concurrent acquires must complete; otherwise the saturation precondition "
                + "for the MaxSize cap test is invalid");
            pool.InUse.Should().Be(5);
            pool.CurrentTotal.Should().Be(5, "MaxSize=5 must cap _total");

            // Try a 6th — should park (Wait behavior).
            using var perCall = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Func<Task> attempt = async () => await pool.AcquireAsync(perCall.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();
            pool.CurrentTotal.Should().Be(5, "_total still capped — no grow past MaxSize");

            // Release everyone.
            holders.Release(5);
            await Task.WhenAll(holdTasks);
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_RecordsCounterAndSpan()
    {
        var (pool, sp, poolName) = Build(b => b
            .WithBounds(minSize: 0, maxSize: 5, initialSize: 0)
            .GrowOnWaiterCount(1));
        try
        {
            using var growCounter = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(),
                "Oragon.ElasticPool", "pool.grow.count");
            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await using var item = await pool.AcquireAsync();

            growCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(1, "exactly one grow");
            var growSpans = captured.ByNameAndPool("Pool.Grow", poolName);
            growSpans.Should().ContainSingle();
            var span = growSpans[0];
            span.GetTagItem("pool.size_after").Should().Be(1);
            span.GetTagItem("grow.tripped_by_waiters").Should().Be(true);
            span.Tags.Should().Contain(t => t.Key == "outcome" && (string?)t.Value == "grew");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }
}

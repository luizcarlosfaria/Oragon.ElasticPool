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

namespace Oragon.ElasticPool.Tests.Telemetry;

public class Phase2CountersAndHistogramsTests
{
    private const string PoolName = "metrics-pool";
    private const string MeterName = "Oragon.ElasticPool";

    private static (ServiceProvider sp, IElasticPool<Resource> pool) BuildKeyed(
        Action<ElasticPoolBuilder<Resource>> configure)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(PoolName, b =>
        {
            b.Factory((s, ct) => ValueTask.FromResult(new Resource()));
            configure(b);
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredKeyedService<IElasticPool<Resource>>(PoolName));
    }

    [Fact(Timeout = 15_000)]
    public async Task GrowCount_IncrementsExactlyOncePerGrow()
    {
        var (sp, pool) = BuildKeyed(b => b.WithBounds(0, 10, 0).GrowOnWaiterCount(1));
        try
        {
            using var collector = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(), MeterName, "pool.grow.count");

            await using (await pool.AcquireAsync()) { }
            await using (await pool.AcquireAsync()) { } // second acquire — already in idle, fast path
            await using (await pool.AcquireAsync()) { } // third acquire — fast path

            // Only the first acquire grew. Subsequent acquires reused the existing idle item.
            var snapshot = collector.GetMeasurementSnapshot();
            snapshot.Sum(m => m.Value).Should().Be(1, "exactly one grow for the cold-start acquire");
            snapshot.Should().AllSatisfy(m => m.Tags.Should().Contain(t => t.Key == "pool.name"));
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task ShrinkCount_IncrementsExactlyOncePerShrink()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sp, ipool) = BuildKeyed(b => b
            .WithBounds(1, 10, 3)
            .WithTimeProvider(fake)
            .IdleTimeout(TimeSpan.FromSeconds(10))
            .ShrinkCooldownWindows(0)
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var pool = (ElasticPool<Resource>)ipool;
        try
        {
            using var collector = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(), MeterName, "pool.shrink.count");

            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            fake.Advance(TimeSpan.FromSeconds(15));
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            collector.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(1);
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task HealthFailures_IncrementsOnEachUnhealthyVerdict()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(PoolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(5, 10, 5)
            .WithTimeProvider(fake)
            .Check((r, ct) =>
            {
                var c = Interlocked.Increment(ref calls);
                // 2 unhealthy, 3 healthy on 5 idle items
                return ValueTask.FromResult(c <= 2 ? PoolState.Unhealthy : PoolState.Healthy);
            })
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var sp = services.BuildServiceProvider();
        try
        {
            using var collector = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(), MeterName, "pool.health.failures");

            var pool = (ElasticPool<Resource>)sp.GetRequiredKeyedService<IElasticPool<Resource>>(PoolName);
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            collector.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(2,
                "counter increments once per Unhealthy verdict (2/5 in this test)");
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task AcquireWaitDuration_RecordsWaiterDurations()
    {
        var (sp, pool) = BuildKeyed(b => b
            .WithBounds(0, 1, 0)
            .GrowOnWaiterCount(1));
        try
        {
            using var collector = new MetricCollector<double>(
                sp.GetRequiredService<IMeterFactory>(), MeterName, "pool.acquire.wait.duration");

            // Hold the only item, then make a 2nd acquire wait for ~50ms.
            var holder = await pool.AcquireAsync();
            var waiter = pool.AcquireAsync().AsTask();
            await Task.Delay(50);
            await holder.DisposeAsync();
            await using var item2 = await waiter;

            var snapshot = collector.GetMeasurementSnapshot();
            snapshot.Should().NotBeEmpty();
            snapshot.Should().Contain(m => m.Value > 0);
            snapshot.Should().AllSatisfy(m => m.Tags.Should().Contain(t => t.Key == "pool.name"));
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task SweepDuration_RecordsOnEveryTick()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sp, ipool) = BuildKeyed(b => b
            .WithBounds(3, 10, 3)
            .WithTimeProvider(fake)
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var pool = (ElasticPool<Resource>)ipool;
        try
        {
            using var collector = new MetricCollector<double>(
                sp.GetRequiredService<IMeterFactory>(), MeterName, "pool.sweep.duration");

            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            for (int i = 0; i < 3; i++)
                await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            var snapshot = collector.GetMeasurementSnapshot();
            snapshot.Count.Should().BeGreaterThanOrEqualTo(3, "one measurement per sweep tick");
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task AllInstrumentsTaggedWithPoolName()
    {
        var (sp, pool) = BuildKeyed(b => b.WithBounds(0, 5, 0).GrowOnWaiterCount(1));
        try
        {
            var meterFactory = sp.GetRequiredService<IMeterFactory>();
            using var grow = new MetricCollector<long>(meterFactory, MeterName, "pool.grow.count");
            using var acquire = new MetricCollector<long>(meterFactory, MeterName, "pool.acquire.count");

            await using (await pool.AcquireAsync()) { }

            grow.GetMeasurementSnapshot().Should().AllSatisfy(m =>
                m.Tags.Should().Contain(t => t.Key == "pool.name"));
            acquire.GetMeasurementSnapshot().Should().AllSatisfy(m =>
                m.Tags.Should().Contain(t => t.Key == "pool.name"));
        }
        finally { sp.Dispose(); }
    }
}

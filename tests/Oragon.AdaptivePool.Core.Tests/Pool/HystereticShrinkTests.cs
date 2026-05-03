using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Oragon.AdaptivePool.Core.Internals;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class HystereticShrinkTests
{
    private static (AdaptivePool<Resource> pool, ServiceProvider sp, string poolName) Build(
        FakeTimeProvider fake,
        Action<AdaptivePoolBuilder<Resource>> configure,
        Func<Resource, CancellationToken, ValueTask>? release = null)
    {
        var poolName = $"shrink-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddAdaptivePool<Resource>(poolName, b =>
        {
            b.Factory((s, ct) => ValueTask.FromResult(new Resource()))
             .WithTimeProvider(fake);
            if (release is not null) b.Release((r, ct) => release(r, ct));
            configure(b);
        });
        var sp = services.BuildServiceProvider();
        var pool = (AdaptivePool<Resource>)sp.GetRequiredKeyedService<IAdaptivePool<Resource>>(poolName);
        return (pool, sp, poolName);
    }

    [Fact(Timeout = 30_000)]
    public async Task Shrink_DoesNotFire_DuringCooldownWindows()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp, poolName) = Build(fake, b => b
            .WithBounds(minSize: 1, maxSize: 10, initialSize: 5)
            .IdleTimeout(TimeSpan.FromSeconds(10))
            .ShrinkCooldownWindows(3)
            .SweepInterval(TimeSpan.FromSeconds(30)));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);
            pool.Available.Should().Be(5);

            // Move all idle items past IdleTimeout.
            fake.Advance(TimeSpan.FromSeconds(15));

            // Tick 1: cooldown counter still < 3 (we just primed; SinceLastGrowTicks starts at 0).
            // Note: warmup grows the pool so SinceLastGrowTicks resets to 0 in the grow path.
            // Plan 02 confirms: SinceLastGrowTicks is reset on every grow. After warmup,
            // SinceLastGrowTicks should be 0 — first sweep tick increments it to 1, etc.
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(5, "tick 1: cooldown=1 (<3), no shrink");

            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(5, "tick 2: cooldown=2 (<3), no shrink");

            // Tick 3 — SinceLastGrowTicks was 2 BEFORE this tick body runs; it must satisfy >= 3
            // BEFORE shrink runs, so we need a 4th tick for actual shrink. Check sweep order:
            // body sequence: (1) health-check pass (2) shrink pass (3) IncrementSinceLastGrowTicks.
            // So shrink reads SinceLastGrowTicks BEFORE incrementing. After 2 ticks, value is 2.
            // Tick 3 sees value 2 (still <3), increments to 3 — no shrink.
            // Tick 4 sees value 3 (>=3) — shrinks.
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(5, "tick 3: cooldown=2 going in, no shrink");

            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(4, "tick 4: cooldown reached >=3, exactly one item evicted (gentle decay)");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Shrink_NeverGoesBelowMinSize()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp, poolName) = Build(fake, b => b
            .WithBounds(minSize: 2, maxSize: 10, initialSize: 5)
            .IdleTimeout(TimeSpan.FromSeconds(10))
            .ShrinkCooldownWindows(0)               // no cooldown — shrink eligible from tick 1
            .SweepInterval(TimeSpan.FromSeconds(30)));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);
            pool.Available.Should().Be(5);

            fake.Advance(TimeSpan.FromSeconds(15)); // age all idle items

            // 10 ticks; gentle decay = 1 per tick. Should saturate at MinSize=2.
            for (int i = 0; i < 10; i++)
            {
                await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            }

            pool.Available.Should().Be(2, "shrink must respect MinSize floor");
            pool.CurrentTotal.Should().Be(2);
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Shrink_OnlyEvictsItemsPastIdleTimeout()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (pool, sp, poolName) = Build(fake, b => b
            .WithBounds(minSize: 1, maxSize: 10, initialSize: 3)
            .IdleTimeout(TimeSpan.FromSeconds(60))
            .ShrinkCooldownWindows(0)
            .SweepInterval(TimeSpan.FromSeconds(30)));
        try
        {
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);
            pool.Available.Should().Be(3);

            // Advance 30s; all 3 items have LastReturnedAt = startTime, so age = 30s < 60s timeout.
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(3, "no items past IdleTimeout yet");

            // Advance one more sweep interval; items aged 60s — exactly at the IdleTimeout
            // boundary (head.LastReturnedAt + IdleTimeout <= now). One stale item evicted.
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
            pool.Available.Should().Be(2, "first stale item evicted (gentle decay = 1 per tick)");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Shrink_RecordsCounterAndSpan_AndReleaseHook()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var releaseCalls = 0;
        var (pool, sp, poolName) = Build(fake, b => b
            .WithBounds(minSize: 1, maxSize: 10, initialSize: 3)
            .IdleTimeout(TimeSpan.FromSeconds(10))
            .ShrinkCooldownWindows(0)
            .SweepInterval(TimeSpan.FromSeconds(30)),
            release: (r, ct) =>
            {
                Interlocked.Increment(ref releaseCalls);
                return ValueTask.CompletedTask;
            });
        try
        {
            using var shrinkCounter = new MetricCollector<long>(
                sp.GetRequiredService<IMeterFactory>(),
                "Oragon.AdaptivePool", "pool.shrink.count");
            using var captured = new CapturedActivities("Oragon.AdaptivePool");

            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            fake.Advance(TimeSpan.FromSeconds(15));
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            pool.Available.Should().Be(2);

            shrinkCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(1);
            var shrinkSpans = captured.ByNameAndPool("Pool.Shrink", poolName);
            shrinkSpans.Should().ContainSingle();
            shrinkSpans[0].GetTagItem("pool.size_before").Should().Be(3);
            shrinkSpans[0].GetTagItem("pool.size_after").Should().Be(2);
            releaseCalls.Should().Be(1, "Release hook fires on the evicted item");
        }
        finally
        {
            await pool.DisposeAsync();
            sp.Dispose();
        }
    }
}

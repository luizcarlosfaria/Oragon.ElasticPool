using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Internals;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

public class BackgroundSweepTests
{
    private static ElasticPool<Resource> BuildPool(
        FakeTimeProvider fake,
        TimeSpan? sweepInterval = null,
        Func<Resource, CancellationToken, ValueTask<PoolState>>? check = null,
        int min = 3,
        int max = 10,
        int initial = 3)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: min, maxSize: max, initialSize: initial)
            .WithTimeProvider(fake)
            .SweepInterval(sweepInterval ?? TimeSpan.FromSeconds(30));
        if (check is not null) builder = builder.Check((r, ct) => check(r, ct));
        return (ElasticPool<Resource>)builder.Build();
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_FiresOnAdvance_AndCallsCheckHookOnIdleItems()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var checkCalls = 0;
        await using var pool = BuildPool(fake, check: (r, ct) =>
        {
            Interlocked.Increment(ref checkCalls);
            return ValueTask.FromResult(PoolState.Healthy);
        });
        await pool.ReadyAsync();
        await SweepDeterminism.PrimeAsync(pool);
        pool.Available.Should().Be(3);

        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

        checkCalls.Should().Be(3, "the Check hook must fire once per idle item on each sweep tick");
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_DoesNotInvokeCheck_WhenNoneConfigured()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var pool = BuildPool(fake);                 // no Check configured
        await pool.ReadyAsync();
        await SweepDeterminism.PrimeAsync(pool);

        var beforeTickCount = Volatile.Read(ref pool.Sweeper.TickCount);
        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

        Volatile.Read(ref pool.Sweeper.TickCount).Should().BeGreaterThan(beforeTickCount);
        pool.Available.Should().Be(3); // no items evicted (cooldown not elapsed)
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_StoppedOnDispose_TerminatesCleanly()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pool = BuildPool(fake);
        await pool.ReadyAsync();
        await SweepDeterminism.PrimeAsync(pool);

        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));
        var tickAfterFirst = Volatile.Read(ref pool.Sweeper.TickCount);
        tickAfterFirst.Should().BeGreaterThan(0);

        await pool.DisposeAsync();

        // After dispose, advancing time should NOT continue ticking — sweep loop is canceled.
        fake.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(100); // very brief, gives any stray completion a chance to land
        Volatile.Read(ref pool.Sweeper.TickCount).Should().Be(tickAfterFirst, "sweeper must not tick after DisposeAsync");
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_CheckUnhealthyItem_NotServedOnNextAcquire_CR03Regression()
    {
        // CR-03 regression: when the Check hook reports Unhealthy, the entry MUST NOT be
        // returned by a subsequent AcquireAsync — independently of the shrink cooldown gate.
        // The previous implementation only marked LastReturnedAt = MinValue and deferred
        // eviction to the cooldown-gated shrink pass, leaving broken items acquirable for up
        // to ShrinkCooldownWindows ticks (default: 3 → up to 90s with default sweep interval).
        //
        // Test: pool has 1 idle item; Check hook reports Unhealthy on that specific instance;
        // run ONE sweep tick (cooldown=3 — would have deferred eviction); then AcquireAsync
        // MUST NOT return the broken instance. With the fix, AcquireAsync grows a fresh one.
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Resource? brokenInstance = null;
        await using var pool = BuildPool(fake,
            check: (r, ct) =>
            {
                if (brokenInstance is null) brokenInstance = r;
                return ValueTask.FromResult(r == brokenInstance ? PoolState.Unhealthy : PoolState.Healthy);
            },
            min: 0, max: 5, initial: 1);
        await pool.ReadyAsync();
        await SweepDeterminism.PrimeAsync(pool);
        pool.Available.Should().Be(1);

        // ONE tick. The default ShrinkCooldownWindows=3 means the shrink pass would NOT have
        // evicted yet under the old (buggy) behavior. CR-03 fix evicts unhealthy items
        // independently of cooldown.
        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

        // Acquire — must NOT return brokenInstance.
        await using var acquired = await pool.AcquireAsync();
        acquired.Value.Should().NotBeNull();
        acquired.Value.Should().NotBeSameAs(brokenInstance,
            "CR-03: AcquireAsync must NEVER return an instance the Check hook flagged Unhealthy, " +
            "regardless of ShrinkCooldownWindows. The old code deferred eviction to the cooldown-gated " +
            "shrink pass, leaving broken items acquirable for up to ~90s.");
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_RunsAtConfiguredInterval()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var pool = BuildPool(fake, sweepInterval: TimeSpan.FromSeconds(15));
        await pool.ReadyAsync();
        await SweepDeterminism.PrimeAsync(pool);

        var initialTicks = Volatile.Read(ref pool.Sweeper.TickCount);

        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(15));
        await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(15));

        Volatile.Read(ref pool.Sweeper.TickCount).Should().Be(initialTicks + 2, "two 15s advances should produce two ticks");
    }
}

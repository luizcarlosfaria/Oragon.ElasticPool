using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Oragon.AdaptivePool.Core.Internals;
using Xunit;

namespace Oragon.AdaptivePool.Core.Stress;

public class BurstIdleBurstStressTest
{
    private sealed class Resource : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Phase 2 anchor gate (ROADMAP success criterion 4 + RESEARCH §"Stress Test Design").
    ///
    /// Burst → Idle → Burst cycle:
    ///   1. Burst 1: 200 threads × 10 iterations = 2000 acquire/release pairs.
    ///      Pool grows under contention from MinSize=5 to (some N) > MinSize.
    ///   2. Idle: FakeTimeProvider drives sweep ticks past IdleTimeout + ShrinkCooldownWindows
    ///      until pool shrinks back to MinSize.
    ///   3. Burst 2: another 200 × 10 — pool regrows without losing waiters.
    ///
    /// Proves the elastic path under contention: composite-signal grow, hysteretic shrink,
    /// no deadlocks, counter-snapshot consistency at end. Watchdog 45s logical / 60s xUnit.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BurstIdleBurst_PoolGrowsShrinksGrowsAgain_WithoutDeadlocks()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        var poolName = "burst-stress";
        services.AddAdaptivePool<Resource>(poolName, b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) => { r.Dispose(); return ValueTask.CompletedTask; })
            .WithBounds(minSize: 5, maxSize: 100, initialSize: 5)
            .WithTimeProvider(fake)
            .SweepInterval(TimeSpan.FromSeconds(30))
            .IdleTimeout(TimeSpan.FromSeconds(60))
            .ShrinkCooldownWindows(3)
            .GrowOnWaiterCount(1));
        await using var sp = services.BuildServiceProvider();

        using var growCounter = new MetricCollector<long>(
            sp.GetRequiredService<IMeterFactory>(), "Oragon.AdaptivePool", "pool.grow.count");
        using var shrinkCounter = new MetricCollector<long>(
            sp.GetRequiredService<IMeterFactory>(), "Oragon.AdaptivePool", "pool.shrink.count");

        var pool = (AdaptivePool<Resource>)sp.GetRequiredKeyedService<IAdaptivePool<Resource>>(poolName);
        await pool.ReadyAsync();

        // Prime the sweep loop so it reaches WaitForNextTickAsync before the first Advance.
        await Task.Yield();
        await Task.Delay(100);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        // === Burst 1 ===
        var burst1 = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                using var perCall = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
                perCall.CancelAfter(TimeSpan.FromSeconds(5));
                await using var item = await pool.AcquireAsync(perCall.Token);
                await Task.Yield();
            }
        }, watchdog.Token)).ToArray();
        await Task.WhenAll(burst1);

        pool.InUse.Should().Be(0);
        var afterBurst1 = pool.Available;
        afterBurst1.Should().BeGreaterThan(5, "pool must grow under burst contention");
        growCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().BeGreaterThan(0,
            "at least one grow recorded by pool.grow.count");

        // === Idle (drive sweep) ===
        // To shrink: need cooldown elapsed (3 sweep windows since last grow) AND IdleTimeout passed.
        // First mark idle entries past IdleTimeout (60s). Then a series of sweep ticks (each 30s)
        // will count down the cooldown and gentle-decay-shrink one item per tick.
        fake.Advance(TimeSpan.FromSeconds(60));

        // Each sweep tick: AdvanceAndAwait pattern — capture pre-tick TCS, advance, await.
        // Iterations: enough to shrink from afterBurst1 down to MinSize=5.
        var iters = (afterBurst1 - 5) + 5; // worst case: cooldown + 1 per tick + safety margin
        for (int i = 0; i < iters && pool.Available > 5; i++)
        {
            var tcs = pool.Sweeper.TickCompleted;
            fake.Advance(TimeSpan.FromSeconds(30));
            var winner = await Task.WhenAny(tcs, Task.Delay(2000, watchdog.Token));
            if (winner != tcs) break;
        }

        pool.Available.Should().Be(5, "pool must shrink back to MinSize after idle");
        shrinkCounter.GetMeasurementSnapshot().Sum(m => m.Value).Should().BeGreaterThan(0,
            "at least one shrink recorded by pool.shrink.count");

        // === Burst 2 ===
        var burst2 = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                using var perCall = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
                perCall.CancelAfter(TimeSpan.FromSeconds(5));
                await using var item = await pool.AcquireAsync(perCall.Token);
                await Task.Yield();
            }
        }, watchdog.Token)).ToArray();
        await Task.WhenAll(burst2);

        // === Final invariants ===
        pool.InUse.Should().Be(0, "no item should be checked out after both bursts");
        pool.Available.Should().BeGreaterThan(5, "pool regrew on second burst");
        // Counter consistency: Available + InUse must equal CurrentTotal (no ghost reservations).
        (pool.Available + pool.InUse).Should().Be(pool.CurrentTotal,
            "counter snapshot consistency: idle + in-use must equal total");
    }
}

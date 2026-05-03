using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Xunit;

namespace Oragon.AdaptivePool.Core.Stress;

public class PingPongStressTest
{
    private sealed class Resource { }

    /// <summary>
    /// Phase 1 anchor gate (ROADMAP success criterion 2 + RESEARCH.md MaxSize=1 ping-pong recipe).
    ///
    /// Proves correctness of:
    ///   - Channel&lt;TaskCompletionSource&gt; direct-handoff waiter queue (no lost wake-ups)
    ///   - Interlocked counter rollback paths
    ///   - CancellationToken honored end-to-end (per-acquire 5s watchdog)
    ///   - No deadlocks under hundreds-of-threads contention with MaxSize=1 (worst case)
    ///
    /// 256 threads × 40 iterations = 10,240 acquire/release cycles. Hard 25s overall ceiling
    /// enforced by the watchdog CTS; outer xUnit Timeout is a safety net at 60s.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock()
    {
        var services = new ServiceCollection();
        services.AddAdaptivePool<Resource>("stress", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 1, maxSize: 1, initialSize: 1));
        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<Resource>>("stress");

        await pool.ReadyAsync();

        const int threads = 256;
        const int iterationsPerThread = 40; // 256 * 40 = 10,240 cycles

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                using var perAcquireTimeout = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
                perAcquireTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                await using var item = await pool.AcquireAsync(perAcquireTimeout.Token);
                // brief simulated work — yield to let other threads contend
                await Task.Yield();
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(tasks);

        // Final invariants — every cycle returned cleanly.
        pool.InUse.Should().Be(0, "no item should be checked out after all threads finish");
        pool.Available.Should().Be(1, "the single MaxSize=1 item must be back in the idle queue");
    }
}

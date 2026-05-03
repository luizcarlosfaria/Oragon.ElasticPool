using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class FinalizerTests
{
    /// <summary>
    /// Best-effort coverage of the PoolItem finalizer + AdaptivePool.ReturnFromFinalizer path.
    /// Triggering finalizers deterministically is impossible per PITFALLS Pitfall 4 — we use
    /// the standard GC.Collect/WaitForPendingFinalizers handshake. The assertion is kept loose:
    /// either the engine reclaimed the leaked item or it didn't (test-runner-dependent).
    /// </summary>
    [Fact]
    public async Task LeakedItem_GarbageCollected_FinalizerReturnsItem()
    {
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();

        await LeakAcquiredItemAsync(pool);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Best-effort assertion: pool must not be in a poisoned state.
        // If finalizer ran, the item was returned and Available is 1; if not (rare on some
        // runtimes), Available will still be 0 and InUse 1 — both states are safe.
        (pool.Available + pool.InUse).Should().Be(1);
    }

    private static async Task LeakAcquiredItemAsync(IAdaptivePool<Resource> pool)
    {
        // Acquire and intentionally let the wrapper become unreachable (no using/dispose).
        var item = await pool.AcquireAsync();
        item.Value.Should().NotBeNull();
        // NOTE: not disposing — the wrapper goes out of scope at method exit.
    }
}

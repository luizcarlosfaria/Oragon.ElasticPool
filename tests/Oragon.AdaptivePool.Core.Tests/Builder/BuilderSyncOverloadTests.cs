using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Hooks;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Builder;

/// <summary>
/// Covers the synchronous overloads of <see cref="AdaptivePoolBuilder{T}"/> hooks
/// (Factory / BeforeUse / Check / AfterUse / Release) and the null-check tightening
/// applied to the asynchronous overloads of BeforeUse / Check / AfterUse / Release.
/// </summary>
public class BuilderSyncOverloadTests
{
    private static IServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    // ------------------------------------------------------------------
    // Synchronous overloads — end-to-end behavior
    // ------------------------------------------------------------------

    [Fact]
    public async Task Factory_SyncOverload_ProducesItemsThroughPool()
    {
        var created = 0;
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                Interlocked.Increment(ref created);
                return new Resource();
            })
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        created.Should().Be(1, "sync Factory overload must produce the warm-up item");
        pool.Available.Should().Be(1, "item returns to idle after Dispose");
    }

    [Fact]
    public async Task BeforeUse_SyncOverload_HealthyState_AllowsAcquire()
    {
        var beforeUseCalls = 0;
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            .BeforeUse((r, ct) =>
            {
                Interlocked.Increment(ref beforeUseCalls);
                return PoolState.Healthy;
            })
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        beforeUseCalls.Should().BeGreaterThan(0, "sync BeforeUse must run on borrow");
        pool.Available.Should().Be(1);
    }

    [Fact]
    public async Task BeforeUse_SyncOverload_Unhealthy_DiscardsAndInvokesRelease()
    {
        var releasedIds = new List<int>();
        var releaseLock = new object();
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            // First borrow always Unhealthy, replacement is Healthy.
            .BeforeUse(MakeAlternatingBeforeUse())
            .Release((r, ct) =>
            {
                lock (releaseLock) releasedIds.Add(r.Id);
            })
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        lock (releaseLock)
        {
            releasedIds.Should().NotBeEmpty(
                "BeforeUse=Unhealthy on the warm-up item must invoke sync Release on the discarded resource");
        }
    }

    [Fact]
    public async Task Check_SyncOverload_StoredOnOptions()
    {
        // Phase 1: Check is not invoked by the engine, but registering it with the sync
        // overload must succeed, satisfy null-check, and not break Build().
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            .Check((r, ct) => PoolState.Healthy)
            .WithBounds(0, 1, 0)
            .Build();

        await pool.ReadyAsync();
        pool.MaxSize.Should().Be(1);
    }

    [Fact]
    public async Task AfterUse_SyncOverload_UnhealthyReturn_DiscardsItem()
    {
        var releasedIds = new List<int>();
        var releaseLock = new object();
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            .AfterUse((r, ct) => PoolState.Unhealthy)
            .Release((r, ct) =>
            {
                lock (releaseLock) releasedIds.Add(r.Id);
            })
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        pool.Available.Should().Be(0, "sync AfterUse Unhealthy must discard the item");
        pool.InUse.Should().Be(0);
        lock (releaseLock)
        {
            releasedIds.Should().HaveCount(1, "sync Release runs on the discarded item");
        }
    }

    [Fact]
    public async Task Release_SyncOverload_InvokedOnDiscard()
    {
        var releaseCalls = 0;
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            .AfterUse((r, ct) => PoolState.Unhealthy)
            .Release((r, ct) => Interlocked.Increment(ref releaseCalls))
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        releaseCalls.Should().Be(1, "sync Release must be invoked exactly once on the discarded item");
    }

    [Fact]
    public async Task Release_SyncOverload_ExceptionDoesNotEscapeAcquire()
    {
        // Engine swallows Release-hook exceptions on the discard paths to keep Acquire/Return non-throwing.
        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => new Resource())
            .AfterUse((r, ct) => PoolState.Unhealthy)
            .Release((r, ct) => throw new InvalidOperationException("Release boom"))
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();

        Func<Task> act = async () =>
        {
            await using (await pool.AcquireAsync()) { }
        };

        await act.Should().NotThrowAsync(
            "sync Release exceptions must be swallowed by the engine on AfterUse-Unhealthy discard");
    }

    // ------------------------------------------------------------------
    // Synchronous overloads — null check
    // ------------------------------------------------------------------

    [Fact]
    public void Factory_SyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.Factory((FactorySyncDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BeforeUse_SyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.BeforeUse((BeforeUseSyncDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Check_SyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.Check((CheckSyncDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AfterUse_SyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.AfterUse((AfterUseSyncDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Release_SyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.Release((ReleaseSyncDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // Async overloads — null check (latent bug fix; previously silent)
    // ------------------------------------------------------------------

    [Fact]
    public void BeforeUse_AsyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.BeforeUse((BeforeUseDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Check_AsyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.Check((CheckDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AfterUse_AsyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.AfterUse((AfterUseDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Release_AsyncOverload_NullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.Release((ReleaseDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // Mixed scenario — sync and async overloads in the same pool
    // ------------------------------------------------------------------

    [Fact]
    public async Task Mixed_FactoryAsync_BeforeUseSync_ReleaseSync_AllInteroperateCorrectly()
    {
        var factoryCalls = 0;
        var beforeUseCalls = 0;
        var releaseCalls = 0;
        var releaseLock = new object();

        var sp = EmptyProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            // Async Factory — simulates a hook that genuinely needs await.
            .Factory(async (s, ct) =>
            {
                Interlocked.Increment(ref factoryCalls);
                await Task.Yield();
                return new Resource();
            })
            // Sync BeforeUse — pure in-memory check.
            .BeforeUse((r, ct) =>
            {
                Interlocked.Increment(ref beforeUseCalls);
                return PoolState.Healthy;
            })
            // Sync Release — IDisposable-style cleanup.
            .Release((r, ct) =>
            {
                lock (releaseLock) releaseCalls++;
            })
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        factoryCalls.Should().BeGreaterThan(0, "async Factory must produce the warm-up item");
        beforeUseCalls.Should().BeGreaterThan(0, "sync BeforeUse must run on borrow");
        pool.Available.Should().Be(1, "Healthy borrow returns the item to idle");
        // Release only fires on discard; with all-Healthy hooks none should run here.
        releaseCalls.Should().Be(0, "sync Release must NOT run when the item stays Healthy");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a sync BeforeUse delegate that flips on each call: first invocation
    /// reports Unhealthy (forces a discard + replacement); subsequent invocations
    /// report Healthy (so the engine's bounded retry loop can settle).
    /// </summary>
    private static BeforeUseSyncDelegate<Resource> MakeAlternatingBeforeUse()
    {
        int calls = 0;
        return (_, _) =>
            Interlocked.Increment(ref calls) == 1
                ? PoolState.Unhealthy
                : PoolState.Healthy;
    }
}

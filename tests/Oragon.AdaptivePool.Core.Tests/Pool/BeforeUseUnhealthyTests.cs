using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class BeforeUseUnhealthyTests
{
    [Fact]
    public async Task BeforeUseUnhealthy_InvokesFailurePolicy_WithFailureKindBeforeUseUnhealthy()
    {
        var policy = Substitute.For<IItemFailurePolicy<Resource>>();
        policy.HandleAsync(Arg.Any<Resource?>(), Arg.Any<FailureKind>(), Arg.Any<Exception?>(), Arg.Any<CancellationToken>())
              .Returns(ValueTask.FromResult(FailureDecision.Discard));

        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource { BrokenFlag = Interlocked.Increment(ref calls) == 1 }))
            .BeforeUse((r, ct) => ValueTask.FromResult(r.BrokenFlag ? PoolState.Unhealthy : PoolState.Healthy))
            .WithBounds(minSize: 0, maxSize: 2, initialSize: 0)
            .WithFailurePolicy(policy)
            .Build();

        await using var item = await pool.AcquireAsync();

        await policy.Received(1).HandleAsync(
            Arg.Any<Resource?>(),
            FailureKind.BeforeUseUnhealthy,
            null,
            Arg.Any<CancellationToken>());
        item.Value.BrokenFlag.Should().BeFalse();
    }

    [Fact]
    public async Task BeforeUseUnhealthy_DiscardAndReplace_DefaultPolicy_ProducesFreshItem()
    {
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource { BrokenFlag = Interlocked.Increment(ref calls) == 1 }))
            .BeforeUse((r, ct) => ValueTask.FromResult(r.BrokenFlag ? PoolState.Unhealthy : PoolState.Healthy))
            .WithBounds(minSize: 0, maxSize: 2, initialSize: 0)
            .Build();

        await using var item = await pool.AcquireAsync();

        item.Value.BrokenFlag.Should().BeFalse("default DiscardAndReplace policy should swap broken for fresh");
        calls.Should().Be(2, "Factory must have run twice — first item discarded, second served");
    }

    [Fact]
    public async Task BeforeUseUnhealthy_InvokesReleaseHookOnDiscardedItem()
    {
        var calls = 0;
        var releasedIds = new List<int>();
        var releaseLock = new object();
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource { BrokenFlag = Interlocked.Increment(ref calls) == 1 }))
            .BeforeUse((r, ct) => ValueTask.FromResult(r.BrokenFlag ? PoolState.Unhealthy : PoolState.Healthy))
            .Release((r, ct) =>
            {
                lock (releaseLock) releasedIds.Add(r.Id);
                return ValueTask.CompletedTask;
            })
            .WithBounds(minSize: 0, maxSize: 2, initialSize: 0)
            .Build();

        await using var item = await pool.AcquireAsync();

        // Give the engine a beat to invoke Release on the discarded broken item
        // (release runs inline in the BeforeUse-Unhealthy branch — already awaited).
        lock (releaseLock)
        {
            releasedIds.Should().HaveCount(1, "Release must be invoked on the discarded broken item");
        }
    }

    [Fact]
    public async Task BeforeUseHealthy_DoesNotInvokePolicyOrRelease()
    {
        var policy = Substitute.For<IItemFailurePolicy<Resource>>();
        policy.HandleAsync(Arg.Any<Resource?>(), Arg.Any<FailureKind>(), Arg.Any<Exception?>(), Arg.Any<CancellationToken>())
              .Returns(ValueTask.FromResult(FailureDecision.Discard));
        var releaseCount = 0;

        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .BeforeUse((r, ct) => ValueTask.FromResult(PoolState.Healthy))
            .Release((r, ct) => { Interlocked.Increment(ref releaseCount); return ValueTask.CompletedTask; })
            .WithBounds(0, 1, 0)
            .WithFailurePolicy(policy)
            .Build();

        await using var item = await pool.AcquireAsync();

        await policy.DidNotReceive().HandleAsync(
            Arg.Any<Resource?>(), Arg.Any<FailureKind>(), Arg.Any<Exception?>(), Arg.Any<CancellationToken>());
        releaseCount.Should().Be(0, "Release should not be invoked while item is healthy and in-use");
    }

    [Fact]
    public async Task BeforeUseUnhealthy_PersistentlyBroken_RetryLimitTriggersInvalidOperation()
    {
        // WR-04 regression: when Factory always succeeds but BeforeUse always returns Unhealthy,
        // PrepareForUseAsync must NOT recurse forever. After the bounded retry limit, callers
        // receive a meaningful InvalidOperationException naming the pool.
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .BeforeUse((r, ct) => ValueTask.FromResult(PoolState.Unhealthy))
            .WithBounds(0, 100, 0) // big enough that growth never hits MaxSize
            .Build();

        var act = async () => await pool.AcquireAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BeforeUse returned Unhealthy*consecutively*");
    }

    [Fact]
    public async Task SyncAcquire_BeforeUseUnhealthy_DiscardsAndTriesNext()
    {
        // CR-04 regression: sync Acquire() must honor BeforeUse — when an idle item is rejected,
        // the engine must discard, try the next idle entry, and only throw PoolExhausted once
        // idle is empty (sync MUST NEVER block awaiting growth).
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource { BrokenFlag = Interlocked.Increment(ref calls) == 1 }))
            .BeforeUse((r, ct) => ValueTask.FromResult(r.BrokenFlag ? PoolState.Unhealthy : PoolState.Healthy))
            .WithBounds(0, 2, 2)
            .Build();

        await pool.ReadyAsync();

        // Two warmup items in idle: one broken, one healthy. Sync Acquire must skip broken
        // and return the healthy one.
        using var item = pool.Acquire();

        item.Value.BrokenFlag.Should().BeFalse("sync Acquire must reject Unhealthy items via BeforeUse");
    }

    [Fact]
    public async Task SyncAcquire_AllIdleUnhealthy_ThrowsPoolExhaustedNotBlocked()
    {
        // CR-04 follow-on: sync Acquire MUST NOT block awaiting growth. When ALL idle items
        // are rejected by BeforeUse, the method throws PoolExhausted rather than calling factory.
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .BeforeUse((r, ct) => ValueTask.FromResult(PoolState.Unhealthy))
            .WithBounds(0, 2, 2)
            .Build();

        await pool.ReadyAsync();

        Action act = () => pool.Acquire();
        act.Should().Throw<Oragon.AdaptivePool.Core.Exceptions.PoolExhaustedException>();
    }

    [Fact]
    public async Task BeforeUseUnhealthyRecursion_AfterDispose_DoesNotMaskObjectDisposed()
    {
        // CR-02 regression (narrow): the recursive AcquireAsync call inside the BeforeUse-Unhealthy
        // branch must use the CALLER's cancellation token, not the composite (which already includes
        // _lifetimeCts.Token). With CR-02's fix, after disposal the recursion enters AcquireAsyncCore,
        // hits ThrowIfDisposed(), and surfaces ObjectDisposedException — not OperationCanceledException.
        var sp = new ServiceCollection().BuildServiceProvider();

        var beforeUseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .BeforeUse(async (r, ct) =>
            {
                // Block here so the test can dispose the pool while we're inside BeforeUse.
                // We DO observe the composite ct (correct — hooks should respect dispose).
                try { await beforeUseGate.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { /* expected on dispose */ }
                return PoolState.Unhealthy;
            })
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var acquireTask = pool.AcquireAsync().AsTask();
        await Task.Delay(100);
        acquireTask.IsCompleted.Should().BeFalse();

        // Dispose the pool while BeforeUse is parked. The composite ct cancels.
        // BeforeUse returns Unhealthy → recursive AcquireAsyncCore(callerCt) → ThrowIfDisposed → ODE.
        var disposeTask = pool.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acquireTask);
        await disposeTask;
    }

    [Fact]
    public async Task BeforeUseThrows_TreatedAsUnhealthy_AndPolicyInvoked()
    {
        var policy = Substitute.For<IItemFailurePolicy<Resource>>();
        policy.HandleAsync(Arg.Any<Resource?>(), Arg.Any<FailureKind>(), Arg.Any<Exception?>(), Arg.Any<CancellationToken>())
              .Returns(ValueTask.FromResult(FailureDecision.Discard));

        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .BeforeUse((r, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("BeforeUse boom");
                return ValueTask.FromResult(PoolState.Healthy);
            })
            .WithBounds(0, 2, 0)
            .WithFailurePolicy(policy)
            .Build();

        await using var item = await pool.AcquireAsync();

        await policy.Received(1).HandleAsync(
            Arg.Any<Resource?>(),
            FailureKind.BeforeUseUnhealthy,
            null,
            Arg.Any<CancellationToken>());
    }
}

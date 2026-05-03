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

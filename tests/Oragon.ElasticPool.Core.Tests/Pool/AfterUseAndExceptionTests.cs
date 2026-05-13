using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.Exceptions;
using Oragon.ElasticPool.Core.Hooks;
using Oragon.ElasticPool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Pool;

public class AfterUseAndExceptionTests
{
    [Fact]
    public async Task AfterUse_HealthyReturn_KeepsItemInPool()
    {
        var afterUseCalls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .AfterUse((r, ct) => { Interlocked.Increment(ref afterUseCalls); return ValueTask.FromResult(PoolState.Healthy); })
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        afterUseCalls.Should().Be(1);
        pool.Available.Should().Be(1);
    }

    [Fact]
    public async Task AfterUse_UnhealthyReturn_DiscardsItem()
    {
        var releasedIds = new List<int>();
        var releaseLock = new object();
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .AfterUse((r, ct) => ValueTask.FromResult(PoolState.Unhealthy))
            .Release((r, ct) =>
            {
                lock (releaseLock) releasedIds.Add(r.Id);
                return ValueTask.CompletedTask;
            })
            .WithBounds(0, 2, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        // After unhealthy, item should NOT return to idle queue, and Release was invoked.
        pool.Available.Should().Be(0, "AfterUse Unhealthy must discard the item, not return it to idle");
        pool.InUse.Should().Be(0);
        lock (releaseLock)
        {
            releasedIds.Should().HaveCount(1, "Release runs on the discarded AfterUse-Unhealthy item");
        }
    }

    [Fact]
    public async Task AfterUse_Throws_ItemStillReturned()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .AfterUse((AfterUseDelegate<Resource>)((r, ct) => throw new InvalidOperationException("AfterUse boom")))
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        // Engine swallows AfterUse exceptions and falls through to ReturnSync.
        pool.Available.Should().Be(1);
    }

    [Fact]
    public void PoolExhaustedException_WithWaitTime_PreservesProperty()
    {
        var ex = new PoolExhaustedException(maxSize: 4, waitTime: TimeSpan.FromMilliseconds(500));

        ex.MaxSize.Should().Be(4);
        ex.WaitTime.Should().Be(TimeSpan.FromMilliseconds(500));
        ex.Message.Should().Contain("MaxSize=4");
        ex.Message.Should().Contain("waited=");
    }

    [Fact]
    public void PoolExhaustedException_WithoutWaitTime_HasNullWaitTime()
    {
        var ex = new PoolExhaustedException(maxSize: 2);

        ex.MaxSize.Should().Be(2);
        ex.WaitTime.Should().BeNull();
        ex.Message.Should().NotContain("waited=");
    }

    [Fact]
    public async Task DisposeAsync_ReleaseHookThrows_DrainContinues_AndIsLogged()
    {
        // Plug a real LoggerFactory so the [LoggerMessage] source-gen path executes.
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var releaseCalls = 0;
        var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) =>
            {
                Interlocked.Increment(ref releaseCalls);
                throw new InvalidOperationException("release boom");
            })
            .WithBounds(0, 3, 3)
            .Build();

        await pool.ReadyAsync();

        // Dispose should drain all 3 items; per-entry exceptions are swallowed and logged.
        Func<Task> act = async () => await pool.DisposeAsync();
        await act.Should().NotThrowAsync();

        releaseCalls.Should().Be(3, "drain must continue past per-item Release exceptions");
    }

    [Fact]
    public async Task AfterUseUnhealthy_WithParkedWaiter_AtMaxSizeOne_WakesWaiterViaReplacement()
    {
        // CR-01 regression: with MaxSize=1 and AfterUse=Unhealthy, returning the only checked-out
        // item must NOT leave a parked waiter blocked forever. The pool must grow a replacement
        // and hand it off to the waiter.
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .AfterUse((r, ct) => ValueTask.FromResult(PoolState.Unhealthy))
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();

        // First acquire — consumes the only slot.
        var first = await pool.AcquireAsync();

        // Second caller parks as a waiter (MaxSize already reached).
        using var waiterCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondTask = pool.AcquireAsync(waiterCts.Token).AsTask();

        // Confirm the second caller is genuinely parked (not yet completed).
        await Task.Delay(50);
        secondTask.IsCompleted.Should().BeFalse("second caller must be parked while MaxSize=1 is saturated");

        // Return first item — AfterUse marks Unhealthy, _total decrements, slot frees.
        // Without CR-01 fix: waiter hangs forever. With fix: pool grows a replacement and hands off.
        await first.DisposeAsync();

        // Waiter must complete within the timeout.
        var second = await secondTask;
        second.Should().NotBeNull();
        second.Value.Should().NotBeNull();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWhilePoolHasInFlightItem_TryReleaseFireAndForget_DoesNotThrow()
    {
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
        var releaseCount = 0;
        var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) => { Interlocked.Increment(ref releaseCount); return ValueTask.CompletedTask; })
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        var item = await pool.AcquireAsync();

        // Dispose while one item is checked out.
        var disposeTask = pool.DisposeAsync().AsTask();
        await disposeTask;

        // Disposing the still-held item routes to TryReleaseFireAndForget (lifecycle != Open).
        await item.DisposeAsync();

        // Allow the fire-and-forget task to schedule.
        await Task.Delay(100);

        // The single idle item Released during drain + the in-flight item Released after.
        releaseCount.Should().BeGreaterThanOrEqualTo(1);
    }
}

using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Exceptions;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

public class AcquireAndReturnTests
{
    private static IElasticPool<Resource> BuildPool(
        int min, int max, int initial,
        Func<int>? factoryCounter = null)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                _ = factoryCounter?.Invoke();
                return ValueTask.FromResult(new Resource());
            })
            .WithBounds(min, max, initial)
            .Build();
    }

    [Fact]
    public async Task Acquire_FastPath_ReturnsItemImmediately()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        using var item = pool.Acquire();

        item.Value.Should().NotBeNull();
        pool.Total.Should().Be(1);
        pool.InUse.Should().Be(1);
        pool.Available.Should().Be(0);
        pool.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task Acquire_NoFreeItem_ThrowsPoolExhausted()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        using var first = pool.Acquire();

        Action act = () => pool.Acquire();

        act.Should().Throw<PoolExhaustedException>()
           .Which.MaxSize.Should().Be(1);
    }

    [Fact]
    public async Task AcquireAsync_FastPath_ReturnsItemImmediately()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        await using var item = await pool.AcquireAsync();

        item.Value.Should().NotBeNull();
        pool.InUse.Should().Be(1);
    }

    [Fact]
    public async Task AcquireAsync_GrowsToMaxSize_OnDemand()
    {
        var calls = 0;
        await using var pool = BuildPool(min: 0, max: 3, initial: 0, () => Interlocked.Increment(ref calls));

        var item1 = await pool.AcquireAsync();
        var item2 = await pool.AcquireAsync();
        var item3 = await pool.AcquireAsync();

        try
        {
            calls.Should().Be(3);
            pool.InUse.Should().Be(3);
        }
        finally
        {
            await item1.DisposeAsync();
            await item2.DisposeAsync();
            await item3.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_WaiterWokenByReturn_DirectHandoff()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        var first = await pool.AcquireAsync();
        var firstId = first.Value.Id;
        pool.Total.Should().Be(1);
        pool.InUse.Should().Be(1);
        pool.Waiting.Should().Be(0);

        // Start a waiter while the only slot is taken.
        var waiterTask = Task.Run(async () => await pool.AcquireAsync());

        // Yield + small delay so the waiter is parked in the channel before we return.
        await Task.Delay(50);
        waiterTask.IsCompleted.Should().BeFalse("the second AcquireAsync must be waiting since MaxSize=1");
        pool.Total.Should().Be(1);
        pool.Waiting.Should().Be(1);

        // Returning the only slot should hand it off directly to the waiter.
        await first.DisposeAsync();

        var second = await waiterTask.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            second.Value.Id.Should().Be(firstId, "direct-handoff should reuse the same pooled instance");
            pool.Total.Should().Be(1);
            pool.InUse.Should().Be(1);
            pool.Waiting.Should().Be(0);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task AwaitUsing_ReturnsItemToPool()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        await using (var item = await pool.AcquireAsync())
        {
            item.Value.Should().NotBeNull();
            pool.InUse.Should().Be(1);
        }

        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
        pool.Total.Should().Be(1);
        pool.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task SyncDispose_ReturnsItemToPool()
    {
        await using var pool = BuildPool(min: 0, max: 1, initial: 1);
        await pool.ReadyAsync();

        var item = pool.Acquire();
        pool.InUse.Should().Be(1);
        item.Dispose();

        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
    }
}

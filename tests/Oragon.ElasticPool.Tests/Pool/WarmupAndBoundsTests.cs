using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Hooks;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

public class WarmupAndBoundsTests
{
    [Fact]
    public async Task ReadyAsync_CompletesWhenWarmedUp()
    {
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(new Resource());
            })
            .WithBounds(minSize: 0, maxSize: 5, initialSize: 3)
            .Build();

        await pool.ReadyAsync();

        pool.Available.Should().Be(3);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ReadyAsync_PropagatesFactoryException()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((FactoryDelegate<Resource>)((s, ct) => throw new InvalidOperationException("warmup boom")))
            .WithBounds(0, 5, 2)
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(async () => await pool.ReadyAsync());
    }

    [Fact]
    public async Task PoolDispose_DuringWarmup_CancelsWarmupTask()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory(async (s, ct) =>
            {
                await Task.Delay(1000, ct);
                return new Resource();
            })
            .WithBounds(0, 10, 10)
            .Build();

        // Dispose while warmup is in flight.
        await Task.Delay(50);
        await pool.DisposeAsync();

        // ReadyAsync should observe cancellation/failure rather than completing successfully.
        await Assert.ThrowsAnyAsync<Exception>(async () => await pool.ReadyAsync());
    }

    [Fact]
    public async Task InitialSizeZero_NoFactoryCallsAtStartup()
    {
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(new Resource());
            })
            .WithBounds(0, 5, 0)
            .Build();

        await pool.ReadyAsync();

        pool.Available.Should().Be(0);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadyAsync_IsAwaitableMultipleTimes()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 3, 2)
            .Build();

        await pool.ReadyAsync();
        await pool.ReadyAsync();   // second await is a no-op (Task<T> is reusable)

        pool.Available.Should().Be(2);
    }
}

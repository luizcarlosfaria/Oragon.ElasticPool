using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Pool;

public class PoolItemDisposeTests
{
    private static IElasticPool<Resource> BuildSinglePool()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 1)
            .Build();
    }

    [Fact]
    public async Task Dispose_IsIdempotent_NoDoubleReturn()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = pool.Acquire();
        item.Dispose();
        item.Dispose(); // second call must be a no-op

        pool.Available.Should().Be(1, "the same item must NOT be returned twice");
        pool.InUse.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = await pool.AcquireAsync();
        await item.DisposeAsync();
        await item.DisposeAsync();

        pool.Available.Should().Be(1);
        pool.InUse.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsyncAfterDispose_IsNoOp()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = await pool.AcquireAsync();
        item.Dispose();
        await item.DisposeAsync(); // must not double-return

        pool.Available.Should().Be(1);
        pool.InUse.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAfterDisposeAsync_IsNoOp()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = await pool.AcquireAsync();
        await item.DisposeAsync();
        item.Dispose();

        pool.Available.Should().Be(1);
        pool.InUse.Should().Be(0);
    }

    [Fact]
    public async Task Value_AfterDispose_ThrowsObjectDisposedException()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = pool.Acquire();
        item.Dispose();

        Action act = () => { _ = item.Value; };

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Value_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        await using var pool = BuildSinglePool();
        await pool.ReadyAsync();

        var item = await pool.AcquireAsync();
        await item.DisposeAsync();

        Action act = () => { _ = item.Value; };

        act.Should().Throw<ObjectDisposedException>();
    }
}

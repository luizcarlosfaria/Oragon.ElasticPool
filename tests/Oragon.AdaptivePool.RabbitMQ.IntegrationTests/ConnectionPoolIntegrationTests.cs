using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.IntegrationTests;

[Trait("Category", "Integration")]
public class ConnectionPoolIntegrationTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public ConnectionPoolIntegrationTests(RabbitMqContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Name() => $"conn-{Guid.NewGuid():N}";

    [Fact]
    public async Task AcquireAsync_ReturnsOpenConnection_AndReleaseReturnsToPool()
    {
        var name = Name();
        var services = new ServiceCollection();
        services.AddAdaptiveConnectionPool(name,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        var lease = await pool.AcquireAsync();
        lease.Value.IsOpen.Should().BeTrue();
        pool.InUse.Should().Be(1);
        pool.Available.Should().Be(0);

        await lease.DisposeAsync();
        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
    }

    [Fact]
    public async Task PoolGrowsToMaxSize_UnderConcurrentAcquire()
    {
        var name = Name();
        var services = new ServiceCollection();
        services.AddAdaptiveConnectionPool(name,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        // Acquire 4 simultaneously — must succeed and saturate the pool.
        var acquired = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.AcquireAsync().AsTask()));
        try
        {
            pool.InUse.Should().Be(4);
            pool.MaxSize.Should().Be(4);
            acquired.Should().AllSatisfy(l => l.Value.IsOpen.Should().BeTrue());
        }
        finally
        {
            foreach (var l in acquired) await l.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeConnectionPool_DrainsAndCloses()
    {
        var name = Name();
        var services = new ServiceCollection();
        services.AddAdaptiveConnectionPool(name,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 2, 1));

        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);
        await pool.ReadyAsync();

        var lease = await pool.AcquireAsync();
        var conn = lease.Value;
        conn.IsOpen.Should().BeTrue();
        await lease.DisposeAsync();

        // Disposing the SP drains the pool — the underlying IConnection should be closed.
        await sp.DisposeAsync();

        // Allow broker to observe the close.
        await Task.Delay(200);
        conn.IsOpen.Should().BeFalse("connection must be closed when the pool is drained");
    }
}

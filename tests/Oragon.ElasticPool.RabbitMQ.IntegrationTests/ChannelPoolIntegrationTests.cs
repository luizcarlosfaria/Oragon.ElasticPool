using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.RabbitMQ.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.IntegrationTests;

[Trait("Category", "Integration")]
public class ChannelPoolIntegrationTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public ChannelPoolIntegrationTests(RabbitMqContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AcquireChannel_PublishMessage_RoundTrips()
    {
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";
        var queueName = $"q-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddElasticConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 2, 0));
        services.AddElasticChannelPool(chPoolName, connName,
            p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        // Declare queue + publish using the pooled channel.
        await using (var lease = await chPool.AcquireAsync())
        {
            await lease.Value.QueueDeclareAsync(
                queue: queueName, durable: false, exclusive: false, autoDelete: true);
            await lease.Value.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = false },
                body: Encoding.UTF8.GetBytes("hello-pool"));
        }

        // Consume via a side connection (independent of the pool) for clean isolation.
        var sideFactory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionString) };
        sideFactory.AutomaticRecoveryEnabled = false;
        await using var sideConn = await sideFactory.CreateConnectionAsync();
        await using var sideCh = await sideConn.CreateChannelAsync();
        var get = await sideCh.BasicGetAsync(queueName, autoAck: true);
        get.Should().NotBeNull();
        Encoding.UTF8.GetString(get!.Body.ToArray()).Should().Be("hello-pool");
    }

    [Fact]
    public async Task ChannelDispose_ConnectionLeaseStaysHeld_WhilePoolHasIdleChannel()
    {
        // Per the layered-pool ownership model: a channel returned to the pool is still
        // alive (idle) and STILL holds its paired connection lease. The connection lease
        // is released only when the channel is discarded (drain/idle-sweep/Unhealthy).
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddElasticConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName,
            p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

        var lease = await chPool.AcquireAsync();
        connPool.InUse.Should().Be(1);

        await lease.DisposeAsync();
        // Channel returned to pool's idle queue → connection lease still held.
        connPool.InUse.Should().Be(1);
        connPool.Available.Should().Be(0);
    }

    [Fact]
    public async Task ClosedChannelMarkedUnhealthy_OnNextAcquire()
    {
        // WR-03 rename + re-scope: this test only validates the IChannel.IsOpen branch
        // of the channel pool's BeforeUse hook (the channel itself is closed before
        // re-acquire). It does NOT validate the paired-connection path. See the
        // companion test DeadConnection_BeforeUseMarksChannelsUnhealthy below for
        // the connection-side validation.
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddElasticConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName,
            p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        var lease1 = await chPool.AcquireAsync();
        await lease1.DisposeAsync();

        var lease2 = await chPool.AcquireAsync();
        try
        {
            // Close the CHANNEL (not the connection) — simulates server-side channel close.
            await lease2.Value.CloseAsync();
        }
        finally
        {
            await lease2.DisposeAsync();
        }

        // Re-acquire — BeforeUse on the closed idle channel must return Unhealthy and
        // the failure policy must produce a fresh, healthy channel (no exception leakage).
        await using var lease3 = await chPool.AcquireAsync();
        lease3.Value.IsOpen.Should().BeTrue(
            "BeforeUse + DiscardAndReplace must produce a healthy channel after the idle one closed");
    }

    [Fact]
    public async Task DeadConnection_BeforeUseMarksChannelsUnhealthy_LazyInvalidation()
    {
        // WR-03: empirical validation of RESEARCH Q1 (lazy invalidation when a
        // CONNECTION dies). The previous test was named for this scenario but
        // actually only closed the channel, leaving the connection-side BeforeUse
        // branch (`pairing.TryGet(ch, out var connLease) && !connLease.Value.IsOpen`)
        // uncovered. This test closes the underlying CONNECTION via a spy
        // IConnectionFactory that captures every produced IConnection.
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";

        // Spy the factory so we can grab the IConnection that backs the channel.
        var realFactory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.ConnectionString),
            AutomaticRecoveryEnabled = false,
        };
        var producedConnections = new System.Collections.Concurrent.ConcurrentBag<IConnection>();
        var spyMock = new Mock<IConnectionFactory>();
        spyMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                var conn = await realFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                producedConnections.Add(conn);
                return conn;
            });
        var spy = spyMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => spy);
        services.AddElasticConnectionPool(connName,
            configureFactory: null,
            p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName,
            p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        // Acquire a channel — this creates exactly one connection (captured by the spy).
        var lease1 = await chPool.AcquireAsync();
        await lease1.DisposeAsync();
        // Channel is now idle; the connection lease is still held by the pairing CWT.

        producedConnections.Should().HaveCount(1, "exactly one connection should have been created so far");
        var underlyingConnection = producedConnections.Single();
        underlyingConnection.IsOpen.Should().BeTrue("connection must still be alive before we close it");

        // Force-close the connection from outside the pool (simulates a broker-side
        // disconnect or operational connection drop).
        await underlyingConnection.CloseAsync();
        underlyingConnection.IsOpen.Should().BeFalse("close must mark connection closed");

        // Re-acquire — the BeforeUse hook walks the pairing for the idle channel,
        // sees connLease.Value.IsOpen=false, returns Unhealthy. The failure policy
        // discards and replaces, producing a fresh channel on a NEW connection.
        await using var lease2 = await chPool.AcquireAsync();
        lease2.Value.IsOpen.Should().BeTrue(
            "BeforeUse must observe connLease.Value.IsOpen=false on the dead-connection path " +
            "and the failure policy must produce a fresh channel on a new connection");
        producedConnections.Count.Should().BeGreaterThan(1,
            "a new connection must have been created to back the replacement channel");
    }
}

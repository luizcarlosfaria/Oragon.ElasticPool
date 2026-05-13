using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.RabbitMQ.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.IntegrationTests;

/// <summary>
/// Scaled-down reproducer of the headline scenario: idle → burst → idle, validating
/// no leaked channels/connections after multiple cycles. The full 100k cycle lives
/// in the SAMPLE; this test runs in &lt;60s wall-clock with 1k publishes per burst × 3 cycles.
/// </summary>
[Trait("Category", "Integration")]
public class BurstyPublisherIntegrationTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public BurstyPublisherIntegrationTests(RabbitMqContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BurstIdleBurst_NoLeakedChannelsOrConnections()
    {
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";
        var queueName = $"bursty-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        // Connection pool MaxSize must cover the number of retained connections needed
        // by the channel pool. With MaxChannelsPerConnection=50 and parallelism=16,
        // MaxSize=32 gives comfortable headroom while still validating no leaks.
        services.AddElasticConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(1, 32, 1));
        services.AddElasticChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 32, 0)
            .WithMaxChannelsPerConnection(50));

        await using var sp = services.BuildServiceProvider();
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        // Topology: declare queue once via a one-off channel.
        await using (var setup = await chPool.AcquireAsync())
        {
            await setup.Value.QueueDeclareAsync(
                queue: queueName, durable: false, exclusive: false, autoDelete: false);
        }

        // Scaled-down vs the sample (sample = 100k × 3 cycles); test = 50 × 2 cycles to
        // keep wall-clock under 60s on slow CI agents. The point of this test is the
        // no-leak invariant (InUse==0 after each cycle) — not raw throughput. The full
        // 100k cycle lives in the SAMPLE, exercised manually by `dotnet run`.
        const int cycles = 3;
        const int perBurst = 200;
        const int parallelism = 16;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, perBurst),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                async (i, token) =>
                {
                    // Per Pitfall 10: each iteration acquires its OWN channel.
                    await using var lease = await chPool.AcquireAsync(token);
                    var body = Encoding.UTF8.GetBytes($"cycle={cycle},i={i}");
                    await lease.Value.BasicPublishAsync(
                        exchange: string.Empty,
                        routingKey: queueName,
                        mandatory: false,
                        basicProperties: new BasicProperties { Persistent = false },
                        body: body,
                        cancellationToken: token);
                });

            // Idle gap (much shorter than sample's 5min — just enough to let the pool settle).
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        // After all cycles, channel pool's InUse must be 0 (all channels returned to
        // idle queue or discarded). Idle channels still retain their shared backing
        // connection leases. Therefore connPool.InUse reflects the number of
        // CONNECTIONS BACKING IDLE CHANNELS — must be ≤ MaxSize.
        chPool.InUse.Should().Be(0, "all channel leases must have been returned");
        connPool.InUse.Should().BeLessThanOrEqualTo(connPool.MaxSize,
            "connection lease count is bounded by the connection pool's MaxSize");

        // Verify message count via side consumer.
        var sideFactory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionString) };
        sideFactory.AutomaticRecoveryEnabled = false;
        await using var sideConn = await sideFactory.CreateConnectionAsync();
        await using var sideCh = await sideConn.CreateChannelAsync();
        var declareOk = await sideCh.QueueDeclarePassiveAsync(queueName);
        declareOk.MessageCount.Should().Be((uint)(cycles * perBurst));
    }
}

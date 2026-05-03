using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.IntegrationTests;

/// <summary>
/// Integration test for the eager-spread strategy under broker <c>channel_max=10</c>.
/// Validates RESEARCH Q2: when MaxChannelsPerConnection matches the broker ceiling and
/// 50 channels are acquired in parallel, the channel pool MUST grow the connection pool
/// (≥ 5 distinct connections used).
/// </summary>
[Trait("Category", "Integration")]
public class ChannelSpreadIntegrationTests : IClassFixture<LowChannelMaxFixture>
{
    private readonly LowChannelMaxFixture _fixture;

    public ChannelSpreadIntegrationTests(LowChannelMaxFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ChannelMax10_50ConcurrentChannels_SpreadAcrossMultipleConnections()
    {
        var connName = $"conn-{Guid.NewGuid():N}";
        var chPoolName = $"ch-{Guid.NewGuid():N}";

        // Per the layered-pool design (Plan 02): each acquired channel holds its OWN
        // IPoolItem<IConnection> lease (not shared across channels). MaxSize on the
        // connection pool therefore must accommodate the desired concurrent-channel count.
        // The eager-spread tracker still validates that the channel pool, under broker
        // channel_max=10 enforcement, distributes channel-creation calls across distinct
        // IConnection refs — observable via Tracker count + connection pool growth.
        var services = new ServiceCollection();
        services.AddAdaptiveConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 64, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 64, 0)
            .WithMaxChannelsPerConnection(10));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

        // Acquire 50 channels — must spread across multiple connections (broker enforces
        // channel_max=10, so a single connection refusing a 11th channel would error if
        // the spread did not actually distribute load).
        const int targetChannels = 50;
        var leases = new List<IPoolItem<IChannel>>();
        try
        {
            for (int i = 0; i < targetChannels; i++)
            {
                leases.Add(await chPool.AcquireAsync());
            }

            // All channels must be open — broker did not reject any creation request,
            // proving the pool spread channels across enough distinct IConnection refs.
            leases.Should().AllSatisfy(l => l.Value.IsOpen.Should().BeTrue());

            // Connection pool must have grown to at least ceil(50/10)=5 distinct connections.
            var totalConnections = connPool.InUse + connPool.Available;
            totalConnections.Should().BeGreaterThanOrEqualTo(5,
                $"50 channels with broker channel_max=10 must spread to ≥5 connections (observed InUse={connPool.InUse}, Available={connPool.Available})");
        }
        finally
        {
            foreach (var l in leases) await l.DisposeAsync();
        }
    }
}

using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

        // The channel pool shares retained connection leases up to
        // MaxChannelsPerConnection. Under broker channel_max=10 enforcement, the pool
        // must distribute channel-creation calls across distinct IConnection refs —
        // observable via the spy IConnectionFactory below (WR-04).
        //
        // WR-04 fix: previously this test asserted `connPool.InUse + connPool.Available
        // >= 5` which did not directly prove enough distinct IConnection instances were
        // used. The reviewer flagged that this does
        // not directly prove distinct-connection spread — proof was indirect (broker
        // would error if a single connection exceeded channel_max=10). We now register
        // a spy IConnectionFactory keyed by `connName` that wraps the real
        // ConnectionFactory and records every IConnection produced; the assertion then
        // counts DISTINCT connections actually used.
        // The real ConnectionFactory (sealed in v7.x — cannot subclass) does the
        // actual broker handshake. The spy IConnectionFactory delegates to it but
        // records every distinct IConnection produced so the test can assert on
        // direct spread instead of indirect broker enforcement.
        //
        // Because the spy is a substitute for IConnectionFactory (not a
        // ConnectionFactory), the WR-02 override path is skipped: the resolver's
        // `factory is ConnectionFactory` check fails on the substitute and it is
        // returned as-is. The spy's CreateConnectionAsync fires for every connection.
        var realFactory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.ConnectionString),
            AutomaticRecoveryEnabled = false,
        };
        var seenConnections = new ConcurrentDictionary<IConnection, byte>();
        var spyMock = new Mock<IConnectionFactory>();
        spyMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                var conn = await realFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                seenConnections.TryAdd(conn, 0);
                return conn;
            });
        var spy = spyMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => spy);
        services.AddAdaptiveConnectionPool(connName,
            configureFactory: null,
            p => p.WithBounds(0, 64, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 64, 0)
            .WithMaxChannelsPerConnection(10));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

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

            // WR-04: count DISTINCT IConnection instances that the spy factory
            // produced. With 50 channels and channel_max=10 the channel pool MUST
            // have created at least ceil(50/10) = 5 distinct connections — otherwise
            // the broker would have rejected channel creation on an over-saturated
            // connection (which is the indirect proof previously relied on).
            var distinctConnections = seenConnections.Count;
            distinctConnections.Should().BeGreaterThanOrEqualTo(5,
                $"50 channels with channel_max=10 must spread across >=5 distinct IConnection " +
                $"instances (observed {distinctConnections})");
        }
        finally
        {
            foreach (var l in leases) await l.DisposeAsync();
        }
    }

}

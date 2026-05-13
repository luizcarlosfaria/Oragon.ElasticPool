using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.RabbitMQ.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.IntegrationTests.Fixtures;
using Oragon.ElasticPool.RabbitMQ.IntegrationTests.TestSupport;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.IntegrationTests;

[Trait("Category", "Integration")]
public class AutomaticRecoveryOverrideTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public AutomaticRecoveryOverrideTests(RabbitMqContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConsumerSetsAutomaticRecoveryTrue_AdapterOverridesAndLogs()
    {
        var connName = $"conn-{Guid.NewGuid():N}";
        var captured = new CapturedLogEntries();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));

        // Closure sets AutomaticRecoveryEnabled=true — adapter must override (via clone) + log EventId 2001.
        // Per WR-02 fix in Phase 3 review, the adapter does NOT mutate the shared/keyed factory; it
        // produces a per-acquire clone with AutomaticRecoveryEnabled=false used for the actual
        // CreateConnectionAsync call. The shared `capturedFactory` retains the consumer's setting.
        ConnectionFactory? capturedFactory = null;
        services.AddElasticConnectionPool(connName,
            cf =>
            {
                cf.Uri = new Uri(_fixture.ConnectionString);
                cf.AutomaticRecoveryEnabled = true;
                capturedFactory = cf;
            },
            p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

        await using var lease = await pool.AcquireAsync();

        // The connection succeeded — proves the override was actually applied to the clone used
        // by CreateConnectionAsync (otherwise AutomaticRecoveryEnabled=true would conflict with
        // the pool's lifecycle ownership and either hang on recovery loops or fail differently).
        lease.Value.IsOpen.Should().BeTrue("broker connection must succeed normally despite the override");

        // EventId 2001 fires every time the adapter overrides AutomaticRecoveryEnabled=true → false
        // on a per-acquire clone. This is the authoritative proof that the override happened.
        captured.ByEventId(2001).Should().NotBeEmpty(
            "adapter must log the AutomaticRecoveryEnabled override (EventId 2001) on every clone");

        // The shared/keyed ConnectionFactory MUST NOT be mutated by the adapter (WR-02 fix).
        // The override is applied to a per-acquire clone, never to this shared instance.
        capturedFactory!.AutomaticRecoveryEnabled.Should().BeTrue(
            "adapter must NOT mutate the shared ConnectionFactory (WR-02); the override is applied "
            + "to a per-acquire clone used by CreateConnectionAsync, leaving the consumer-supplied "
            + "configuration intact for any other code that reads it");
    }

    [Fact]
    public async Task PoolReplacesDeadConnection_NotAutomaticRecovery()
    {
        // When a connection is closed mid-flight (we close it directly), the next acquire
        // observes IsOpen=false at BeforeUse and the failure-policy produces a fresh
        // connection — proof the POOL replaces (not RabbitMQ.Client's auto-recovery).
        var connName = $"conn-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddElasticConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

        var lease1 = await pool.AcquireAsync();
        var conn1 = lease1.Value;
        // Close the connection mid-flight (simulates a broker-side disconnect).
        await conn1.CloseAsync();
        conn1.IsOpen.Should().BeFalse();
        await lease1.DisposeAsync();

        // Re-acquire: BeforeUse must mark the dead one Unhealthy and the failure-policy
        // produces a fresh, open IConnection.
        await using var lease2 = await pool.AcquireAsync();
        lease2.Value.IsOpen.Should().BeTrue();
        lease2.Value.Should().NotBeSameAs(conn1);
    }
}

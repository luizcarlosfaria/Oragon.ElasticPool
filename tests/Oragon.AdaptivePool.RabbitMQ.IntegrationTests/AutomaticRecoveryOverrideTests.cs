using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.IntegrationTests.Fixtures;
using Oragon.AdaptivePool.RabbitMQ.IntegrationTests.TestSupport;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.IntegrationTests;

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

        // Closure sets AutomaticRecoveryEnabled=true — adapter must override + log EventId 2001.
        ConnectionFactory? capturedFactory = null;
        services.AddAdaptiveConnectionPool(connName,
            cf =>
            {
                cf.Uri = new Uri(_fixture.ConnectionString);
                cf.AutomaticRecoveryEnabled = true;
                capturedFactory = cf;
            },
            p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

        await using var lease = await pool.AcquireAsync();

        lease.Value.IsOpen.Should().BeTrue("broker connection must succeed normally despite the override");
        captured.ByEventId(2001).Should().NotBeEmpty();
        capturedFactory!.AutomaticRecoveryEnabled.Should().BeFalse(
            "adapter must force AutomaticRecoveryEnabled=false at every Factory invocation");
    }

    [Fact]
    public async Task PoolReplacesDeadConnection_NotAutomaticRecovery()
    {
        // When a connection is closed mid-flight (we close it directly), the next acquire
        // observes IsOpen=false at BeforeUse and the failure-policy produces a fresh
        // connection — proof the POOL replaces (not RabbitMQ.Client's auto-recovery).
        var connName = $"conn-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddAdaptiveConnectionPool(connName,
            cf => cf.Uri = new Uri(_fixture.ConnectionString),
            p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

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

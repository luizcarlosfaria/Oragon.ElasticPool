using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.Tests.TestSupport;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.Tests;

/// <summary>
/// Unit tests for <c>AddAdaptiveConnectionPool</c> DI extension wiring. NSubstitute
/// substitutes <see cref="IConnectionFactory"/> and <see cref="IConnection"/>; the Core
/// pool engine is real (we verify the adapter glues hooks correctly).
/// </summary>
public class ConnectionPoolUnitTests
{
    private static string Name() => $"test-{Guid.NewGuid():N}";

    private static IConnection MakeOpenConn()
    {
        var conn = Substitute.For<IConnection>();
        conn.IsOpen.Returns(true);
        return conn;
    }

    private static IConnectionFactory FactoryReturning(params IConnection[] conns)
    {
        var factory = Substitute.For<IConnectionFactory>();
        if (conns.Length == 1)
        {
            factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conns[0]);
        }
        else
        {
            int idx = 0;
            factory.CreateConnectionAsync(Arg.Any<CancellationToken>())
                .Returns(_ => conns[Math.Min(idx++, conns.Length - 1)]);
        }
        return factory;
    }

    [Fact]
    public async Task AddAdaptiveConnectionPool_RegistersResolvableKeyedSingleton()
    {
        var name = Name();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => FactoryReturning(MakeOpenConn()));
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name).Should().NotBeNull();
    }

    [Fact]
    public async Task AddAdaptiveConnectionPool_EmptyName_AlsoResolvableNonKeyed()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(string.Empty, (_, _) => FactoryReturning(MakeOpenConn()));
        services.AddAdaptiveConnectionPool(string.Empty, configureFactory: null, p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAdaptivePool<IConnection>>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddAdaptiveConnectionPool_FactoryCallsCreateConnectionAsync()
    {
        var name = Name();
        var conn = MakeOpenConn();
        var factory = FactoryReturning(conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => factory);
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        await using var lease = await pool.AcquireAsync();

        lease.Value.Should().BeSameAs(conn);
        await factory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeforeUse_ReturnsUnhealthy_WhenIsOpenFalse_TriggersFactoryReplacement()
    {
        // First acquire returns a "dead" connection (IsOpen=false). The next acquire
        // re-uses the pool, but BeforeUse marks it Unhealthy and the failure-policy
        // discards/replaces — so the Factory is invoked a 2nd time.
        var name = Name();
        var deadConn = Substitute.For<IConnection>();
        deadConn.IsOpen.Returns(false);
        var freshConn = MakeOpenConn();

        var factory = Substitute.For<IConnectionFactory>();
        int callCount = 0;
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ++callCount == 1 ? deadConn : freshConn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => factory);
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        await using var lease = await pool.AcquireAsync();

        // Default DiscardAndReplaceFailurePolicy retries on Unhealthy → 2nd Factory call.
        callCount.Should().BeGreaterThan(1, "Unhealthy item must trigger replacement");
        lease.Value.Should().BeSameAs(freshConn);
    }

    [Fact]
    public async Task Release_CallsCloseAsync_ThenDispose_EvenWhenCloseThrows()
    {
        var name = Name();
        var conn = MakeOpenConn();
        // CloseAsync throws — the adapter must still call DisposeAsync.
        // RabbitMQ.Client's IConnection.CloseAsync(ct) extension delegates to the multi-arg
        // overload (replyCode, replyText, timeout, abort, ct) — match Any.
        conn.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("simulated")));

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => FactoryReturning(conn));
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 1, 0));

        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        var lease = await pool.AcquireAsync();
        await lease.DisposeAsync();

        // Release the pool: triggers final drain (Release hook on idle items).
        await sp.DisposeAsync();

        await conn.Received().CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await conn.Received().DisposeAsync();
    }

    [Fact]
    public async Task AutomaticRecoveryOverride_LogsWarning_OnFirstAcquire()
    {
        // Use the closure mode so a real ConnectionFactory flows through; we sub IConnection
        // by overriding the factory via keyed registration AFTER the closure ran. Actually,
        // simpler: use closure mode but then the resolver returns a real ConnectionFactory
        // which would try to open a real socket. Instead: register a keyed ConnectionFactory
        // (concrete) so ForceAutomaticRecoveryDisabled detects AutomaticRecoveryEnabled=true
        // and overrides it, AND sub the CreateConnectionAsync via... wait — the resolver
        // returns the keyed factory directly. ConnectionFactory.CreateConnectionAsync would
        // actually open a socket. So we must intercept differently.
        //
        // Approach: register concrete ConnectionFactory as keyed IConnectionFactory; the
        // factory hook calls CreateConnectionAsync which fails (no broker) — but the
        // Warning EventId 2001 is emitted BEFORE CreateConnectionAsync (per resolver code).
        // We accept the connect failure, just assert the log entry was captured.
        var name = Name();
        var captured = new CapturedLogEntries();

        var concreteFactory = new ConnectionFactory
        {
            HostName = "127.0.0.1",
            Port = 1, // guaranteed to fail to connect
            AutomaticRecoveryEnabled = true,
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => concreteFactory);
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(name);

        // Acquire will fail (no broker on port 1) but the override + log run synchronously
        // BEFORE CreateConnectionAsync is awaited.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var _ = await pool.AcquireAsync(cts.Token);
        }
        catch
        {
            // expected — the broker connection fails
        }

        captured.ByEventId(2001).Should().NotBeEmpty("Warning EventId=2001 must be emitted when AutomaticRecoveryEnabled=true is overridden");
        concreteFactory.AutomaticRecoveryEnabled.Should().BeFalse("override forces it to false at every acquire");
    }
}

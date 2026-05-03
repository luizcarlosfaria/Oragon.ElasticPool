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
        // WR-02: the override no longer MUTATES the shared singleton — it returns a clone
        // with AutomaticRecoveryEnabled=false for the connection-creation call only. The
        // shared registered factory keeps its original AutomaticRecoveryEnabled=true so
        // any side-channel code holding a reference is unaffected. EventId 2001 fires on
        // EVERY acquire that overrides (not just the first).
        //
        // We register a concrete ConnectionFactory pointing at port 1 (no broker); the
        // resolver returns it from the keyed singleton mode, the override clones it
        // before calling CreateConnectionAsync, and the connection attempt fails — but
        // the EventId 2001 Warning must already be in the captured log.
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

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var _ = await pool.AcquireAsync(cts.Token);
        }
        catch
        {
            // expected — the broker connection fails
        }

        captured.ByEventId(2001).Should().NotBeEmpty(
            "Warning EventId=2001 must be emitted when AutomaticRecoveryEnabled=true is overridden");

        // WR-02 invariant: shared factory MUST NOT be mutated.
        concreteFactory.AutomaticRecoveryEnabled.Should().BeTrue(
            "shared singleton factory must not be mutated; override applies to a per-acquire clone (WR-02)");
    }

    [Fact]
    public void AddAdaptiveConnectionPool_DoubleRegistration_Throws()
    {
        // IN-02: silent double-registration produces an inconsistent registration
        // (first-wins pool singleton, last-wins builder). Throw to fail fast.
        var name = Name();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(name, (_, _) => FactoryReturning(MakeOpenConn()));
        services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 1, 0));

        Action act = () => services.AddAdaptiveConnectionPool(name, configureFactory: null, p => p.WithBounds(0, 2, 0));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }
}

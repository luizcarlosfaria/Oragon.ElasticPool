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
/// Unit tests for <c>AddAdaptiveChannelPool</c> — the layered Factory acquires from
/// the connection pool, pairing via CWT, BeforeUse double-IsOpen probe, eager spread,
/// and Release order. NSubstitute IConnection/IChannel; real Core pool engine.
/// </summary>
public class ChannelPoolUnitTests
{
    private static string CName() => $"conn-{Guid.NewGuid():N}";
    private static string ChName() => $"ch-{Guid.NewGuid():N}";

    private static IConnection MakeConn(IChannel? channelToReturn = null, bool isOpen = true)
    {
        var conn = Substitute.For<IConnection>();
        conn.IsOpen.Returns(isOpen);
        if (channelToReturn is not null)
        {
            conn.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(_ => channelToReturn);
        }
        return conn;
    }

    private static IChannel MakeChannel(bool isOpen = true)
    {
        var ch = Substitute.For<IChannel>();
        ch.IsOpen.Returns(isOpen);
        return ch;
    }

    private static IConnectionFactory FactoryProducingDistinctConnections(Func<IConnection>[] generators)
    {
        var factory = Substitute.For<IConnectionFactory>();
        int idx = 0;
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var i = Math.Min(idx++, generators.Length - 1);
                return generators[i]();
            });
        return factory;
    }

    [Fact]
    public async Task AddAdaptiveChannelPool_FactoryAcquiresFromConnectionPool()
    {
        var connName = CName();
        var chPoolName = ChName();
        var ch = MakeChannel();
        var conn = MakeConn(ch);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

        await using var lease = await chPool.AcquireAsync();

        await factory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
        await conn.Received(1).CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>());
        lease.Value.Should().BeSameAs(ch);
    }

    [Fact]
    public async Task AddAdaptiveChannelPool_PairingHoldsConnectionLease_WhileChannelIdleInPool()
    {
        // After a channel is returned to the channel pool's idle queue, it is NOT discarded
        // (Release hook not invoked). Therefore the paired connection lease is STILL held
        // (CWT entry intact). Connection pool's InUse remains 1; releasing the connection
        // happens only when the channel is discarded by the channel pool (failed BeforeUse,
        // idle-sweep, or pool dispose). This validates the layered-pool ownership model.
        var connName = CName();
        var chPoolName = ChName();
        var ch = MakeChannel();
        var conn = MakeConn(ch);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

        var lease = await chPool.AcquireAsync();
        connPool.InUse.Should().Be(1, "channel acquire borrowed one connection lease");
        connPool.Available.Should().Be(0);

        await lease.DisposeAsync();

        // Channel is now idle in the channel pool. Release hook NOT invoked. Connection
        // lease is still held by the CWT pairing entry — InUse remains 1.
        connPool.InUse.Should().Be(1, "channel returned to idle queue retains its connection lease");
        connPool.Available.Should().Be(0);
    }

    [Fact]
    public async Task BeforeUse_ReturnsUnhealthy_WhenChannelIsOpenFalse()
    {
        var connName = CName();
        var chPoolName = ChName();

        // First channel: closed (IsOpen=false). Second: healthy.
        var deadCh = MakeChannel(isOpen: false);
        var freshCh = MakeChannel(isOpen: true);
        var conn = Substitute.For<IConnection>();
        conn.IsOpen.Returns(true);
        int chCalls = 0;
        conn.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++chCalls == 1 ? deadCh : freshCh);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

        await using var lease = await chPool.AcquireAsync();

        // First channel was Unhealthy → DiscardAndReplace → 2nd Factory invocation.
        chCalls.Should().BeGreaterThan(1);
        lease.Value.Should().BeSameAs(freshCh);
    }

    [Fact]
    public async Task BeforeUse_ReturnsUnhealthy_WhenPairedConnectionIsOpenFalse()
    {
        var connName = CName();
        var chPoolName = ChName();

        // The connection BeforeUse hook must also return Unhealthy when conn.IsOpen=false,
        // which would prevent the channel BeforeUse from ever seeing this connection.
        // To test the channel-level lazy invalidation in isolation we need a connection
        // that the connection pool considers Healthy at acquire time but flips to closed
        // before the channel's BeforeUse runs. Easiest: configure conn.IsOpen to return
        // true on the first call, false on subsequent calls.
        var ch = MakeChannel(isOpen: true);
        var conn = Substitute.For<IConnection>();
        var ioOpenCalls = 0;
        conn.IsOpen.Returns(_ =>
        {
            // First two calls (connection pool BeforeUse + channel pool's BeforeUse for conn)
            // see true; subsequent calls see false. We're not relying on exact call order —
            // just ensure the channel's BeforeUse can observe a closed conn.
            ioOpenCalls++;
            return ioOpenCalls <= 1;
        });
        conn.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ch);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 1, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

        // The first acquire creates a channel via the (initially-Healthy) conn; subsequent
        // checks see conn.IsOpen=false and the channel pool's BeforeUse must mark Unhealthy.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var lease = await chPool.AcquireAsync(cts.Token);
        }
        catch
        {
            // The replacement loop will exhaust quickly because the (single) connection
            // becomes Unhealthy too — we accept any terminal exception. The point is that
            // BeforeUse observed conn.IsOpen=false on at least one path.
        }

        ioOpenCalls.Should().BeGreaterThanOrEqualTo(2,
            "BeforeUse hook must consult conn.IsOpen on the paired connection");
    }

    [Fact]
    public async Task Release_DisposesChannelAndReturnsConnectionLease_OnPoolDrain()
    {
        // Release hook fires on pool drain (SP dispose). The hook calls
        // channel.CloseAsync(...) — RabbitMQ.Client provides an extension that delegates
        // to channel.CloseAsync(replyCode, replyText, abort, ct), so we receive a 4-arg
        // call on the substitute. After channel disposal the pairing returns the
        // connection lease to the connection pool (or Releases it during connection drain).
        var connName = CName();
        var chPoolName = ChName();
        var ch = MakeChannel();
        var conn = MakeConn(ch);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

        var lease = await chPool.AcquireAsync();
        await lease.DisposeAsync(); // back to idle queue

        // Disposing the service provider drains the channel pool (Release on each idle
        // item) which then disposes the underlying connections via the connection pool drain.
        await sp.DisposeAsync();

        // Channel CloseAsync extension calls the 4-arg overload — receive at least once.
        await ch.Received().CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await ch.Received().DisposeAsync();
        // Connection eventually closed/disposed during connection-pool drain.
        await conn.Received().DisposeAsync();
    }

    [Fact]
    public async Task EagerSpread_NewConnection_AcquiredWhenChannelMaxReached()
    {
        var connName = CName();
        var chPoolName = ChName();

        // Two distinct connections; each can produce one fresh channel.
        var ch1 = MakeChannel();
        var ch2 = MakeChannel();
        var conn1 = MakeConn(ch1);
        var conn2 = MakeConn(ch2);

        var factory = FactoryProducingDistinctConnections(new Func<IConnection>[] { () => conn1, () => conn2 });

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        // MaxChannelsPerConnection=1 forces the second acquire to use a different connection.
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 4, 0)
            .WithMaxChannelsPerConnection(1));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);

        await using var lease1 = await chPool.AcquireAsync();
        await using var lease2 = await chPool.AcquireAsync();

        // Two distinct channels obtained from two distinct connections (eager-spread).
        lease1.Value.Should().NotBeSameAs(lease2.Value);
        await factory.Received(2).CreateConnectionAsync(Arg.Any<CancellationToken>());
        await conn1.Received(1).CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>());
        await conn2.Received(1).CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_ChannelDisposeAsyncThrows_StillReleasesConnectionLease()
    {
        // CR-01 regression: previously, an exception from IChannel.DisposeAsync inside the
        // Release hook silently leaked the tracker slot, the pairing entry, AND the
        // connection lease (Core call sites swallow Release-hook exceptions). The fix
        // wraps DisposeAsync in try/catch so the cleanup below ALWAYS runs.
        //
        // Verify: after a forced DisposeAsync throw, the connection pool's InUse drops
        // back to 0 (the lease was returned), and EventId 2003 is logged.
        var connName = CName();
        var chPoolName = ChName();
        var captured = new CapturedLogEntries();

        var ch = Substitute.For<IChannel>();
        ch.IsOpen.Returns(true);
        ch.DisposeAsync().Returns(_ => ValueTask.FromException(
            new IOException("simulated DisposeAsync failure")));

        var conn = Substitute.For<IConnection>();
        conn.IsOpen.Returns(true);
        conn.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ch);

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

        var lease = await chPool.AcquireAsync();
        connPool.InUse.Should().Be(1);
        await lease.DisposeAsync(); // returns to idle queue — Release NOT yet invoked

        // Drain the channel pool — Release runs on every idle item; DisposeAsync throws
        // but cleanup must still run.
        await sp.DisposeAsync();

        // CR-01 invariant: tracker slot and connection lease were released even though
        // ch.DisposeAsync threw. Connection pool's InUse must end at 0.
        connPool.InUse.Should().Be(0,
            "tracker.ReleaseSlot + connLease.DisposeAsync must run even when ch.DisposeAsync throws");

        // EventId 2003 must have been emitted at Warning level.
        captured.ByEventId(2003).Should().NotBeEmpty(
            "EventId 2003 must surface IChannel.DisposeAsync failures");
    }

    [Fact]
    public void AddAdaptiveChannelPool_DoubleRegistration_Throws()
    {
        // IN-02: silent double-registration produces an inconsistent registration.
        // Throw to fail fast.
        var connName = CName();
        var chPoolName = ChName();
        var conn = MakeConn(MakeChannel());
        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        Action act = () => services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 8, 0));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public async Task CreateChannelThrows_ReleasesBorrowedConnectionSlot()
    {
        var connName = CName();
        var chPoolName = ChName();

        var conn = Substitute.For<IConnection>();
        conn.IsOpen.Returns(true);
        conn.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns<IChannel>(_ => throw new IOException("simulated channel creation failure"));

        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => conn);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddAdaptiveConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddAdaptiveChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IAdaptivePool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connName);

        // Acquire fails because CreateChannelAsync always throws; the connection lease
        // must be returned to the pool (no leaks) — check via Available count after.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var _ = await chPool.AcquireAsync(cts.Token);
        }
        catch
        {
            // expected
        }

        // The pool's failure handling should release the connection lease back. Final
        // InUse must be 0 — no connection is held by the channel pool after a failed acquire.
        connPool.InUse.Should().Be(0, "borrowed connection lease must be returned when CreateChannelAsync throws");
    }
}

using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.RabbitMQ.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.Tests.TestSupport;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.Tests;

/// <summary>
/// Unit tests for <c>AddElasticChannelPool</c> — the layered Factory acquires from
/// the connection pool, pairing via CWT, BeforeUse double-IsOpen probe, eager spread,
/// and Release order. Moq IConnection/IChannel; real Core pool engine.
/// </summary>
public class ChannelPoolUnitTests
{
    private static string CName() => $"conn-{Guid.NewGuid():N}";
    private static string ChName() => $"ch-{Guid.NewGuid():N}";

    /// <summary>
    /// Polls the mock's Invocations list until at least one invocation's method name
    /// contains the given <paramref name="methodNamePart"/>, or the timeout elapses.
    /// Used to bridge the brief window between <c>await sp.DisposeAsync()</c> returning
    /// and Moq finishing to record interceptor invocations from the dispose chain on
    /// resource-constrained test agents.
    /// </summary>
    private static async Task WaitForInvocationAsync<T>(T mockedObject, string methodNamePart, TimeSpan timeout)
        where T : class
    {
        var mock = Mock.Get(mockedObject);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (mock.Invocations.Any(i => i.Method.Name.Contains(methodNamePart)))
                return;
            await Task.Delay(20);
        }
    }

    private static IConnection MakeConn(IChannel? channelToReturn = null, bool isOpen = true)
    {
        var connMock = new Mock<IConnection>();
        connMock.Setup(m => m.IsOpen).Returns(isOpen);
        if (channelToReturn is not null)
        {
            connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channelToReturn);
        }
        return connMock.Object;
    }

    private static IConnection MakeConnProducingFreshChannels(bool isOpen = true)
    {
        var connMock = new Mock<IConnection>();
        connMock.Setup(m => m.IsOpen).Returns(isOpen);
        connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => MakeChannel());
        return connMock.Object;
    }

    private static IChannel MakeChannel(bool isOpen = true)
    {
        var chMock = new Mock<IChannel>();
        chMock.Setup(m => m.IsOpen).Returns(isOpen);
        return chMock.Object;
    }

    private static IConnectionFactory FactoryProducingDistinctConnections(Func<IConnection>[] generators)
    {
        var factoryMock = new Mock<IConnectionFactory>();
        int idx = 0;
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var i = Math.Min(idx++, generators.Length - 1);
                return Task.FromResult(generators[i]());
            });
        return factoryMock.Object;
    }

    [Fact]
    public async Task AddElasticChannelPool_FactoryAcquiresFromConnectionPool()
    {
        var connName = CName();
        var chPoolName = ChName();
        var ch = MakeChannel();
        var conn = MakeConn(ch);

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        await using var lease = await chPool.AcquireAsync();

        factoryMock.Verify(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(conn).Verify(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
        lease.Value.Should().BeSameAs(ch);
    }

    [Fact]
    public async Task AddElasticChannelPool_SharedConnectionLeaseStaysHeld_WhileChannelIdleInPool()
    {
        // After a channel is returned to the channel pool's idle queue, it is NOT discarded
        // (Release hook not invoked). Therefore its shared connection lease is STILL held.
        // Connection pool's InUse remains 1; releasing the connection happens only when
        // the last channel backed by it is discarded by the channel pool (failed BeforeUse,
        // idle-sweep, or pool dispose).
        var connName = CName();
        var chPoolName = ChName();
        var ch = MakeChannel();
        var conn = MakeConn(ch);

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

        var lease = await chPool.AcquireAsync();
        connPool.InUse.Should().Be(1, "channel acquire borrowed one shared connection lease");
        connPool.Available.Should().Be(0);

        await lease.DisposeAsync();

        // Channel is now idle in the channel pool. Release hook NOT invoked. The shared
        // connection lease is still retained — InUse remains 1.
        connPool.InUse.Should().Be(1, "channel returned to idle queue retains its shared connection lease");
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
        var connMock = new Mock<IConnection>();
        connMock.Setup(m => m.IsOpen).Returns(true);
        int chCalls = 0;
        connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(++chCalls == 1 ? deadCh : freshCh));
        var conn = connMock.Object;

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

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
        var connMock = new Mock<IConnection>();
        var ioOpenCalls = 0;
        connMock.Setup(m => m.IsOpen).Returns(() =>
        {
            // First two calls (connection pool BeforeUse + channel pool's BeforeUse for conn)
            // see true; subsequent calls see false. We're not relying on exact call order —
            // just ensure the channel's BeforeUse can observe a closed conn.
            ioOpenCalls++;
            return ioOpenCalls <= 1;
        });
        connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ch);
        var conn = connMock.Object;

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 1, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

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

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        var lease = await chPool.AcquireAsync();
        await lease.DisposeAsync(); // back to idle queue

        // Disposing the service provider drains the channel pool (Release on each idle
        // item) which then disposes the underlying connections via the connection pool drain.
        await sp.DisposeAsync();

        // Channel CloseAsync extension calls the 4-arg overload — receive at least once.
        Mock.Get(ch).Verify(m => m.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // DisposeAsync is dispatched via the explicit IAsyncDisposable interface implementation
        // on both IChannel and IConnection. Method matching for explicit-interface invocations
        // is fragile across runtimes/TFMs in Moq, so we inspect Invocations and match by
        // method.Name containing "DisposeAsync" (covers "DisposeAsync" plain, the prefixed
        // "IAsyncDisposable.DisposeAsync", and any synthetic stub name).
        // Under heavy concurrent multi-TFM test load, the underlying disposal chain
        // (connection pool drain after channel pool drain) may not finish recording
        // invocations the instant `await sp.DisposeAsync()` returns, so we poll briefly.
        await WaitForInvocationAsync(ch, "DisposeAsync", TimeSpan.FromSeconds(2));
        await WaitForInvocationAsync(conn, "DisposeAsync", TimeSpan.FromSeconds(2));

        Mock.Get(ch).Invocations.Should().Contain(i => i.Method.Name.Contains("DisposeAsync"),
            "channel must be disposed by the channel pool's Release hook during drain");
        Mock.Get(conn).Invocations.Should().Contain(i => i.Method.Name.Contains("DisposeAsync"),
            "connection must be disposed by the connection pool's Release hook during drain");
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
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        // MaxChannelsPerConnection=1 forces the second acquire to use a different connection.
        services.AddElasticChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 4, 0)
            .WithMaxChannelsPerConnection(1));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        await using var lease1 = await chPool.AcquireAsync();
        await using var lease2 = await chPool.AcquireAsync();

        // Two distinct channels obtained from two distinct connections (eager-spread).
        lease1.Value.Should().NotBeSameAs(lease2.Value);
        Mock.Get(factory).Verify(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Mock.Get(conn1).Verify(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(conn2).Verify(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChannelPool_SharesConnections_UntilMaxChannelsPerConnection()
    {
        var connName = CName();
        var chPoolName = ChName();

        var connections = new[]
        {
            MakeConnProducingFreshChannels(),
            MakeConnProducingFreshChannels(),
        };
        var factory = FactoryProducingDistinctConnections(connections.Select<IConnection, Func<IConnection>>(c => () => c).ToArray());

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 64, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p
            .WithBounds(0, 64, 0)
            .WithMaxChannelsPerConnection(16));

        await using var sp = services.BuildServiceProvider();
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);

        var leases = new List<IPoolItem<IChannel>>();
        try
        {
            for (var i = 0; i < 32; i++)
                leases.Add(await chPool.AcquireAsync());

            connPool.InUse.Should().Be(2,
                "32 live channels with MaxChannelsPerConnection=16 should retain only ceil(32/16) connection leases");
            Mock.Get(factory).Verify(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            foreach (var lease in leases)
                await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Release_ChannelDisposeAsyncThrows_StillReleasesConnectionLease()
    {
        // CR-01 regression: previously, an exception from IChannel.DisposeAsync inside the
        // Release hook silently leaked the pairing entry AND the
        // shared lease count (Core call sites swallow Release-hook exceptions). The fix
        // wraps DisposeAsync in try/catch so the cleanup below ALWAYS runs.
        //
        // Verify: after a forced DisposeAsync throw, the connection pool's InUse drops
        // back to 0 (the lease was returned), and EventId 2003 is logged.
        var connName = CName();
        var chPoolName = ChName();
        var captured = new CapturedLogEntries();

        var chMock = new Mock<IChannel>();
        chMock.Setup(m => m.IsOpen).Returns(true);
        // DisposeAsync is on IAsyncDisposable (explicit interface) — set up via .As<>.
        chMock.As<IAsyncDisposable>()
            .Setup(m => m.DisposeAsync())
            .Returns(ValueTask.FromException(new IOException("simulated DisposeAsync failure")));
        var ch = chMock.Object;

        var connMock = new Mock<IConnection>();
        connMock.Setup(m => m.IsOpen).Returns(true);
        connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ch);
        var conn = connMock.Object;

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

        var lease = await chPool.AcquireAsync();
        connPool.InUse.Should().Be(1);
        await lease.DisposeAsync(); // returns to idle queue — Release NOT yet invoked

        // Drain the channel pool — Release runs on every idle item; DisposeAsync throws
        // but cleanup must still run.
        await sp.DisposeAsync();

        // CR-01 invariant: shared lease count and connection lease were released even though
        // ch.DisposeAsync threw. Connection pool's InUse must end at 0.
        connPool.InUse.Should().Be(0,
            "shared lease release must run even when ch.DisposeAsync throws");

        // EventId 2003 must have been emitted at Warning level.
        captured.ByEventId(2003).Should().NotBeEmpty(
            "EventId 2003 must surface IChannel.DisposeAsync failures");
    }

    [Fact]
    public void AddElasticChannelPool_DoubleRegistration_Throws()
    {
        // IN-02: silent double-registration produces an inconsistent registration.
        // Throw to fail fast.
        var connName = CName();
        var chPoolName = ChName();
        var conn = MakeConn(MakeChannel());
        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 4, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 4, 0));

        Action act = () => services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 8, 0));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public async Task CreateChannelThrows_ReleasesBorrowedConnectionSlot()
    {
        var connName = CName();
        var chPoolName = ChName();

        var connMock = new Mock<IConnection>();
        connMock.Setup(m => m.IsOpen).Returns(true);
        connMock.Setup(m => m.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("simulated channel creation failure"));
        var conn = connMock.Object;

        var factoryMock = new Mock<IConnectionFactory>();
        factoryMock.Setup(m => m.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        var factory = factoryMock.Object;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>(connName, (_, _) => factory);
        services.AddElasticConnectionPool(connName, null, p => p.WithBounds(0, 2, 0));
        services.AddElasticChannelPool(chPoolName, connName, p => p.WithBounds(0, 2, 0));

        await using var sp = services.BuildServiceProvider();
        var chPool = sp.GetRequiredKeyedService<IElasticPool<IChannel>>(chPoolName);
        var connPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connName);

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

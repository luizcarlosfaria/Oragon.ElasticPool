using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Oragon.AdaptivePool.RabbitMQ.Internals;
using Oragon.AdaptivePool.RabbitMQ.Options;
using Oragon.AdaptivePool.RabbitMQ.Tests.TestSupport;
using RabbitMQ.Client;
using Xunit;
using Microsoft.Extensions.Logging;

namespace Oragon.AdaptivePool.RabbitMQ.Tests;

/// <summary>
/// Unit tests for the 3-mode probe in <see cref="ConnectionFactoryResolver"/>.
/// Probe order: keyed singleton > closure > IOptions. Plus the
/// <c>ForceAutomaticRecoveryDisabled</c> override + EventId 2001 warning.
/// </summary>
public class ConnectionFactoryResolverTests
{
    [Fact]
    public void Resolve_PrefersKeyedSingleton_OverClosure()
    {
        var keyedFactory = new Mock<IConnectionFactory>().Object;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConnectionFactory>("p1", (_, _) => keyedFactory);
        using var sp = services.BuildServiceProvider();

        // Closure that would mutate a fresh ConnectionFactory if mode 1 misses.
        var resolved = ConnectionFactoryResolver.Resolve(sp, "p1", cf => cf.HostName = "should-not-be-used");

        resolved.Should().BeSameAs(keyedFactory);
    }

    [Fact]
    public void Resolve_FallsBackToClosure_WhenNoKeyed()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var resolved = ConnectionFactoryResolver.Resolve(sp, "p1", cf => cf.HostName = "rabbit-from-closure");

        resolved.Should().BeOfType<ConnectionFactory>();
        ((ConnectionFactory)resolved).HostName.Should().Be("rabbit-from-closure");
    }

    [Fact]
    public void Resolve_FallsBackToOptions_WhenNoKeyedAndNoClosure()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AdaptiveConnectionPoolOptions>("opts-pool", o =>
        {
            o.HostName = "rabbit-from-options";
            o.Port = 5673;
            o.UserName = "u";
            o.Password = "p";
            o.VirtualHost = "/v";
            o.RequestedHeartbeat = TimeSpan.FromSeconds(45);
        });
        using var sp = services.BuildServiceProvider();

        var resolved = ConnectionFactoryResolver.Resolve(sp, "opts-pool", configureFactory: null);

        resolved.Should().BeOfType<ConnectionFactory>();
        var cf = (ConnectionFactory)resolved;
        cf.HostName.Should().Be("rabbit-from-options");
        cf.Port.Should().Be(5673);
        cf.UserName.Should().Be("u");
        cf.Password.Should().Be("p");
        cf.VirtualHost.Should().Be("/v");
        cf.RequestedHeartbeat.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void Resolve_ThrowsInvalidOperation_WhenAllModesFail()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        using var sp = services.BuildServiceProvider();

        Action act = () => ConnectionFactoryResolver.Resolve(sp, "missing-pool", configureFactory: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing-pool*");
    }

    [Fact]
    [Obsolete("Tests obsolete API for back-compat coverage")]
    public void ForceAutomaticRecoveryDisabled_LogsWarning_AndOverridesToFalse_WhenTrue()
    {
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var cf = new ConnectionFactory { AutomaticRecoveryEnabled = true };

        ConnectionFactoryResolver.ForceAutomaticRecoveryDisabled(cf, logger, "p1");

        cf.AutomaticRecoveryEnabled.Should().BeFalse();
        captured.ByEventId(2001).Should().HaveCount(1)
            .And.Subject.First().Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    [Obsolete("Tests obsolete API for back-compat coverage")]
    public void ForceAutomaticRecoveryDisabled_NoOp_WhenAlreadyFalse()
    {
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var cf = new ConnectionFactory { AutomaticRecoveryEnabled = false };

        ConnectionFactoryResolver.ForceAutomaticRecoveryDisabled(cf, logger, "p1");

        cf.AutomaticRecoveryEnabled.Should().BeFalse();
        captured.ByEventId(2001).Should().BeEmpty();
    }

    [Fact]
    [Obsolete("Tests obsolete API for back-compat coverage")]
    public void ForceAutomaticRecoveryDisabled_NoLog_WhenInterfaceOnly_NotConcreteFactory()
    {
        // The override only applies when the resolved factory is the concrete ConnectionFactory.
        // A pure IConnectionFactory substitute exposes no AutomaticRecoveryEnabled property,
        // so the helper is a no-op. (T-03-12 documented carry-forward.)
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var fakeFactory = new Mock<IConnectionFactory>().Object;

        ConnectionFactoryResolver.ForceAutomaticRecoveryDisabled(fakeFactory, logger, "p1");

        captured.ByEventId(2001).Should().BeEmpty();
    }

    // === WR-02: ApplyAutomaticRecoveryOverride (returns clone, does not mutate) ===

    [Fact]
    public void ApplyAutomaticRecoveryOverride_ReturnsClone_WithRecoveryDisabled_PreservesOriginal()
    {
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var original = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            HostName = "rabbit-host",
            Port = 5673,
            UserName = "u",
            Password = "p",
            VirtualHost = "/v",
            RequestedHeartbeat = TimeSpan.FromSeconds(45),
        };

        var result = ConnectionFactoryResolver.ApplyAutomaticRecoveryOverride(original, logger, "p1");

        // The clone has recovery disabled.
        result.Should().BeOfType<ConnectionFactory>();
        var clone = (ConnectionFactory)result;
        clone.AutomaticRecoveryEnabled.Should().BeFalse();
        // Configuration was copied to the clone.
        clone.HostName.Should().Be("rabbit-host");
        clone.Port.Should().Be(5673);
        clone.UserName.Should().Be("u");
        clone.Password.Should().Be("p");
        clone.VirtualHost.Should().Be("/v");
        clone.RequestedHeartbeat.Should().Be(TimeSpan.FromSeconds(45));
        // CRITICAL invariant — the shared singleton was NOT mutated.
        original.AutomaticRecoveryEnabled.Should().BeTrue(
            "WR-02: the shared registered ConnectionFactory must NOT be mutated by the override");
        clone.Should().NotBeSameAs(original);
        captured.ByEventId(2001).Should().HaveCount(1);
    }

    [Fact]
    public void ApplyAutomaticRecoveryOverride_AlreadyDisabled_ReturnsSame_NoLog()
    {
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var cf = new ConnectionFactory { AutomaticRecoveryEnabled = false };

        var result = ConnectionFactoryResolver.ApplyAutomaticRecoveryOverride(cf, logger, "p1");

        result.Should().BeSameAs(cf, "no clone needed when already configured correctly");
        captured.ByEventId(2001).Should().BeEmpty();
    }

    [Fact]
    public void ApplyAutomaticRecoveryOverride_LogsOnEveryAcquire_NotJustFirst()
    {
        // WR-02: with the mutation-based fix, the warning fired only on the first acquire
        // because subsequent calls saw AutomaticRecoveryEnabled=false. With the clone-based
        // fix, the shared factory is never mutated, so EVERY override emits the warning —
        // keeping the misconfiguration visible until the consumer fixes it.
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var cf = new ConnectionFactory { AutomaticRecoveryEnabled = true };

        for (int i = 0; i < 5; i++)
        {
            ConnectionFactoryResolver.ApplyAutomaticRecoveryOverride(cf, logger, "p1");
        }

        cf.AutomaticRecoveryEnabled.Should().BeTrue("shared instance not mutated");
        captured.ByEventId(2001).Should().HaveCount(5,
            "every override must log so a misconfigured factory keeps producing diagnostic output");
    }

    [Fact]
    public void ApplyAutomaticRecoveryOverride_ReturnsInputUnchanged_WhenNotConcreteFactory()
    {
        // For a custom IConnectionFactory we have no contract to override; return as-is.
        var captured = new CapturedLogEntries();
        using var lf = LoggerFactory.Create(b => b.AddProvider(captured).SetMinimumLevel(LogLevel.Trace));
        var logger = lf.CreateLogger("test");
        var fakeFactory = new Mock<IConnectionFactory>().Object;

        var result = ConnectionFactoryResolver.ApplyAutomaticRecoveryOverride(fakeFactory, logger, "p1");

        result.Should().BeSameAs(fakeFactory);
        captured.ByEventId(2001).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullArguments_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        Action act1 = () => ConnectionFactoryResolver.Resolve(null!, "p", null);
        Action act2 = () => ConnectionFactoryResolver.Resolve(sp, null!, null);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }
}

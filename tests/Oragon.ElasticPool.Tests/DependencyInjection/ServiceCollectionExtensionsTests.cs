using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.DependencyInjection;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddElasticPool_DefaultName_RegistersAsKeyedAndNonKeyed()
    {
        var services = new ServiceCollection();
        services.AddElasticPool<Resource>(string.Empty, b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();

        var keyed = sp.GetRequiredKeyedService<IElasticPool<Resource>>(string.Empty);
        var nonKeyed = sp.GetRequiredService<IElasticPool<Resource>>();

        keyed.Should().BeSameAs(nonKeyed, "default-name registration must expose the same singleton via both lookups");
    }

    [Fact]
    public async Task AddElasticPool_WithName_RegistersAsKeyedOnly()
    {
        var services = new ServiceCollection();
        services.AddElasticPool<Resource>("primary", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 0));

        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IElasticPool<Resource>>("primary").Should().NotBeNull();
        sp.GetService<IElasticPool<Resource>>().Should().BeNull("named-only registration must not leak into the non-keyed slot");
    }

    [Fact]
    public async Task AddElasticPool_TwoNamedPoolsSameType_BothResolve()
    {
        var services = new ServiceCollection();
        services.AddElasticPool<Resource>("primary", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 2, 0));
        services.AddElasticPool<Resource>("secondary", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 5, 0));

        await using var sp = services.BuildServiceProvider();

        var primary = sp.GetRequiredKeyedService<IElasticPool<Resource>>("primary");
        var secondary = sp.GetRequiredKeyedService<IElasticPool<Resource>>("secondary");

        primary.Should().NotBeSameAs(secondary);
        primary.MaxSize.Should().Be(2);
        secondary.MaxSize.Should().Be(5);
    }

    [Theory]
    [InlineData("services")]
    [InlineData("name")]
    [InlineData("configure")]
    public void AddElasticPool_NullArguments_ThrowArgumentNullException(string nullArg)
    {
        IServiceCollection services = nullArg == "services" ? null! : new ServiceCollection();
        string name = nullArg == "name" ? null! : "x";
        Action<Oragon.ElasticPool.Builder.ElasticPoolBuilder<Resource>> configure =
            nullArg == "configure" ? null! : _ => { };

        Action act = () => services.AddElasticPool<Resource>(name, configure);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddElasticPool_HostApplicationLifetimeRegistered_PoolReceivesStoppingToken()
    {
        // CR-03 regression: when IHostApplicationLifetime is registered (as it is in any
        // ASP.NET Core / Generic Host app), the pool's lifetime CT must be wired to its
        // ApplicationStopping token so SIGTERM promptly cancels parked AcquireAsync waiters.
        var fakeLifetime = new FakeHostApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<IHostApplicationLifetime>(fakeLifetime);
        services.AddElasticPool<Resource>(string.Empty, b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IElasticPool<Resource>>();
        await pool.ReadyAsync();

        // Saturate the pool, then park a waiter.
        var first = await pool.AcquireAsync();
        var waiterCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiterTask = pool.AcquireAsync(waiterCts.Token).AsTask();

        await Task.Delay(50);
        waiterTask.IsCompleted.Should().BeFalse("waiter must be parked while MaxSize=1 is saturated");

        // Trigger application shutdown — this flips ApplicationStopping which the pool's
        // lifetime CT is linked to. The parked waiter must observe cancellation promptly.
        fakeLifetime.StopApplication();

        var act = async () => await waiterTask;
        await act.Should().ThrowAsync<Exception>("parked waiter must be cancelled when host stops");

        await first.DisposeAsync();
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    [Fact]
    public async Task AddElasticPool_PoolNameTagFlowsToTelemetry()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddElasticPool<Resource>("metrics-test", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1));

        await using var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.acquire.count");

        var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>("metrics-test");
        await pool.ReadyAsync();
        await using (await pool.AcquireAsync()) { }

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().NotBeEmpty();
        snapshot.Should().Contain(m =>
            m.Tags.ContainsKey("pool.name") && (string)m.Tags["pool.name"]! == "metrics-test");
    }
}

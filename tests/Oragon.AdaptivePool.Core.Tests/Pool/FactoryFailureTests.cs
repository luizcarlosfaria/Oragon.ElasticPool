using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using NSubstitute;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class FactoryFailureTests
{
    [Fact]
    public async Task FactoryThrows_DecrementsTotal_AllowsSubsequentAcquireToReachMaxSize()
    {
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1) throw new InvalidOperationException("first call fails");
                return ValueTask.FromResult(new Resource());
            })
            .WithBounds(minSize: 0, maxSize: 2, initialSize: 0)
            .Build();

        // First call raises — counter should roll back so pool can still grow to MaxSize.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());

        // Second + third must both succeed — proves no "ghost reservation".
        var a = await pool.AcquireAsync();
        var b = await pool.AcquireAsync();

        try
        {
            calls.Should().Be(3);
            pool.InUse.Should().Be(2);
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task FactoryThrows_InvokesFailurePolicy_WithFailureKindFactoryThrew_AndException()
    {
        var policy = Substitute.For<IItemFailurePolicy<Resource>>();
        policy.HandleAsync(default, Arg.Any<FailureKind>(), Arg.Any<Exception?>(), Arg.Any<CancellationToken>())
              .Returns(ValueTask.FromResult(FailureDecision.Discard));

        var boom = new InvalidOperationException("boom");
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => throw boom)
            .WithBounds(0, 1, 0)
            .WithFailurePolicy(policy)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());

        await policy.Received(1).HandleAsync(
            default,
            FailureKind.FactoryThrew,
            Arg.Is<Exception?>(e => ReferenceEquals(e, boom)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FactoryThrows_IncrementsFactoryFailuresCounter()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.AdaptivePool", "pool.factory.failures");

        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => throw new InvalidOperationException("boom"))
            .WithBounds(0, 5, 0)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty();
        measurements.Sum(m => m.Value).Should().Be(1);
    }
}

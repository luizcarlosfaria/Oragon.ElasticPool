using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.Hooks;
using Oragon.ElasticPool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Pool;

public class FactoryFailureTests
{
    [Fact]
    public async Task FactoryThrows_DecrementsTotal_AllowsSubsequentAcquireToReachMaxSize()
    {
        var calls = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
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
        var policyMock = new Mock<IItemFailurePolicy<Resource>>();
        policyMock.Setup(m => m.HandleAsync(default, It.IsAny<FailureKind>(), It.IsAny<Exception?>(), It.IsAny<CancellationToken>()))
                  .Returns(ValueTask.FromResult(FailureDecision.Discard));
        var policy = policyMock.Object;

        var boom = new InvalidOperationException("boom");
        var sp = new ServiceCollection().BuildServiceProvider();
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((FactoryDelegate<Resource>)((s, ct) => throw boom))
            .WithBounds(0, 1, 0)
            .WithFailurePolicy(policy)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());

        policyMock.Verify(m => m.HandleAsync(
            default,
            FailureKind.FactoryThrew,
            It.Is<Exception?>(e => ReferenceEquals(e, boom)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FactoryThrows_IncrementsFactoryFailuresCounter()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.factory.failures");

        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((FactoryDelegate<Resource>)((s, ct) => throw new InvalidOperationException("boom")))
            .WithBounds(0, 5, 0)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty();
        measurements.Sum(m => m.Value).Should().Be(1);
    }
}

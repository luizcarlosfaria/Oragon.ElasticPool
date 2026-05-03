using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Telemetry;

public class MeterAndCounterTests
{
    [Fact]
    public async Task Meter_IsNamedOragonAdaptivePool_AndCreatedViaIMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.AdaptivePool", "pool.acquire.count");

        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();
        await pool.ReadyAsync();

        await using (await pool.AcquireAsync()) { }

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().NotBeEmpty();
        snapshot.Sum(m => m.Value).Should().Be(1);
        snapshot.Should().Contain(m => m.Tags.ContainsKey("pool.name"));
    }

    [Fact]
    public async Task FactoryFailure_IncrementsCounter()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.AdaptivePool", "pool.factory.failures");

        var calls = 0;
        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) =>
            {
                if (Interlocked.Increment(ref calls) <= 3)
                    throw new InvalidOperationException("boom");
                return ValueTask.FromResult(new Resource());
            })
            .WithBounds(0, 5, 0)
            .Build();

        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());
        }

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Sum(m => m.Value).Should().Be(3);
    }

    [Fact]
    public async Task OtelListener_ObservesPoolMeter()
    {
        var observedInstruments = new List<string>();
        var observedValues = new List<long>();
        var lockObj = new object();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Oragon.AdaptivePool")
            {
                lock (lockObj) observedInstruments.Add(instrument.Name);
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (lockObj) observedValues.Add(value);
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();

        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();
        await pool.ReadyAsync();

        await using (await pool.AcquireAsync()) { }

        lock (lockObj)
        {
            observedInstruments.Should().Contain("pool.acquire.count");
            observedValues.Sum().Should().BeGreaterThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task PoolWithoutAddMetrics_FallbackMeterUsed_AndPoolStillFunctions()
    {
        // Bare ServiceCollection — no AddMetrics(); engine must fall back to `new Meter`.
        var sp = new ServiceCollection().BuildServiceProvider();

        var observedInstruments = new List<string>();
        var observedValues = new List<long>();
        var lockObj = new object();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Oragon.AdaptivePool")
            {
                lock (lockObj) observedInstruments.Add(instrument.Name);
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (lockObj) observedValues.Add(value);
        });
        listener.Start();

        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();
        await pool.ReadyAsync();

        await using (await pool.AcquireAsync()) { }

        lock (lockObj)
        {
            observedInstruments.Should().Contain("pool.acquire.count");
            observedValues.Sum().Should().BeGreaterThanOrEqualTo(1);
        }
    }
}

using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Telemetry;

public class MeterAndCounterTests
{
    [Fact]
    public async Task Meter_IsNamedOragonElasticPool_AndCreatedViaIMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();
        var meterFactory = sp.GetRequiredService<IMeterFactory>();

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.acquire.count");

        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
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

        using var collector = new MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.factory.failures");

        var calls = 0;
        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
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
            if (instrument.Meter.Name == "Oragon.ElasticPool")
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

        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
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
    public async Task ObservableGauges_ReportCurrentPoolState()
    {
        var measurements = new List<(string Name, int Value, Dictionary<string, object?> Tags)>();
        var lockObj = new object();
        var gaugeNames = new HashSet<string>
        {
            "pool.size",
            "pool.available",
            "pool.in_use",
            "pool.waiting",
        };

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Oragon.ElasticPool" && gaugeNames.Contains(instrument.Name))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            var copiedTags = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copiedTags[tag.Key] = tag.Value;
            }
            lock (lockObj) measurements.Add((instrument.Name, value, copiedTags));
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddMetrics();
        var sp = services.BuildServiceProvider();

        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();
        await pool.ReadyAsync();

        listener.RecordObservableInstruments();

        var holder = await pool.AcquireAsync();
        var waiter = pool.AcquireAsync().AsTask();
        for (var i = 0; i < 50 && pool.Waiting == 0; i++)
        {
            await Task.Delay(10);
        }

        listener.RecordObservableInstruments();

        lock (lockObj)
        {
            measurements.Should().Contain(m => m.Name == "pool.size" && m.Value == 1);
            measurements.Should().Contain(m => m.Name == "pool.available" && m.Value == 0);
            measurements.Should().Contain(m => m.Name == "pool.in_use" && m.Value == 1);
            measurements.Should().Contain(m => m.Name == "pool.waiting" && m.Value == 1);
            measurements.Should().AllSatisfy(m => m.Tags.Should().ContainKey("pool.name"));
        }

        await holder.DisposeAsync();
        await using var waited = await waiter;
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
            if (instrument.Meter.Name == "Oragon.ElasticPool")
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

        await using var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
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

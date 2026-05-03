using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Oragon.AdaptivePool.Core.Telemetry;

internal sealed class TelemetryEmitter : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _acquireCount;
    private readonly Counter<long> _factoryFailures;
    private readonly KeyValuePair<string, object?> _poolNameTag;
    private readonly bool _ownsMeter;

    public TelemetryEmitter(IServiceProvider services, string poolName)
    {
        var factory = services.GetService<IMeterFactory>();
        if (factory is not null)
        {
            _meter = factory.Create(PoolMeterNames.MeterName);
            _ownsMeter = false;
        }
        else
        {
            _meter = new Meter(PoolMeterNames.MeterName);
            _ownsMeter = true;
        }

        _acquireCount = _meter.CreateCounter<long>(
            PoolMeterNames.AcquireCount,
            unit: "{acquires}",
            description: "Total number of successful Acquire calls.");
        _factoryFailures = _meter.CreateCounter<long>(
            PoolMeterNames.FactoryFailures,
            unit: "{failures}",
            description: "Total number of times the Factory hook threw an exception.");
        _poolNameTag = new KeyValuePair<string, object?>(PoolMeterNames.PoolNameTag, poolName);
    }

    public void OnAcquire() => _acquireCount.Add(1, _poolNameTag);
    public void OnFactoryFailure() => _factoryFailures.Add(1, _poolNameTag);

    public void Dispose()
    {
        if (_ownsMeter) _meter.Dispose();
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Core.Internals;

namespace Oragon.ElasticPool.Core.Telemetry;

internal sealed class TelemetryEmitter : IDisposable
{
    /// <summary>
    /// Process-lifetime ActivitySource shared across every pool instance (RESEARCH Pitfall F).
    /// Listeners attach via <c>builder.AddSource("Oragon.ElasticPool")</c>; without a listener
    /// every <c>StartActivity</c> returns null (cheap no-op). Static — never disposed.
    /// </summary>
    internal static readonly ActivitySource ActivitySource = new(PoolMeterNames.ActivitySourceName);

    private readonly Meter _meter;
    private readonly Counter<long> _acquireCount;
    private readonly Counter<long> _factoryFailures;
    private readonly Counter<long> _growCount;
    private readonly Counter<long> _shrinkCount;
    private readonly Counter<long> _healthFailures;
    private readonly Histogram<double> _acquireWaitDuration;
    private readonly Histogram<double> _sweepDuration;
    private readonly KeyValuePair<string, object?> _poolNameTag;
    private readonly bool _ownsMeter;

    public TelemetryEmitter(
        IServiceProvider services,
        string poolName,
        Func<int> observeTotal,
        Func<int> observeAvailable,
        Func<int> observeInUse,
        Func<int> observeWaiting)
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

        // Phase 2 instruments.
        _growCount = _meter.CreateCounter<long>(
            PoolMeterNames.GrowCount,
            unit: "{grows}",
            description: "Total number of times the pool grew (composite-signal grow + replacement grow after AfterUse=Unhealthy).");
        _shrinkCount = _meter.CreateCounter<long>(
            PoolMeterNames.ShrinkCount,
            unit: "{shrinks}",
            description: "Total number of times the sweep loop evicted an idle item.");
        _healthFailures = _meter.CreateCounter<long>(
            PoolMeterNames.HealthFailures,
            unit: "{failures}",
            description: "Total number of Check-hook Unhealthy verdicts (or thrown exceptions) observed during sweep.");
        _acquireWaitDuration = _meter.CreateHistogram<double>(
            PoolMeterNames.AcquireWaitDuration,
            unit: "s",
            description: "Time a caller waited on the slow path before being handed an item.");
        _sweepDuration = _meter.CreateHistogram<double>(
            PoolMeterNames.SweepDuration,
            unit: "s",
            description: "Wall-clock duration of one sweep tick (health-check pass + shrink pass).");

        _poolNameTag = new KeyValuePair<string, object?>(PoolMeterNames.PoolNameTag, poolName);

        _meter.CreateObservableGauge(
            PoolMeterNames.Size,
            () => new Measurement<int>(observeTotal(), _poolNameTag),
            unit: "{items}",
            description: "Total items the pool currently owns (idle + in-use + being-created).");
        _meter.CreateObservableGauge(
            PoolMeterNames.Available,
            () => new Measurement<int>(observeAvailable(), _poolNameTag),
            unit: "{items}",
            description: "Items currently idle in the pool, available for immediate acquire.");
        _meter.CreateObservableGauge(
            PoolMeterNames.InUse,
            () => new Measurement<int>(observeInUse(), _poolNameTag),
            unit: "{items}",
            description: "Items currently checked out by consumers.");
        _meter.CreateObservableGauge(
            PoolMeterNames.Waiting,
            () => new Measurement<int>(observeWaiting(), _poolNameTag),
            unit: "{waiters}",
            description: "AcquireAsync callers currently waiting for a returned item.");
    }

    public void OnAcquire() => _acquireCount.Add(1, _poolNameTag);
    public void OnFactoryFailure() => _factoryFailures.Add(1, _poolNameTag);
    public void OnGrow() => _growCount.Add(1, _poolNameTag);
    public void OnShrink() => _shrinkCount.Add(1, _poolNameTag);
    public void OnHealthFailure() => _healthFailures.Add(1, _poolNameTag);
    public void OnAcquireWait(TimeSpan duration) => _acquireWaitDuration.Record(duration.TotalSeconds, _poolNameTag);
    public void OnSweepDuration(TimeSpan duration) => _sweepDuration.Record(duration.TotalSeconds, _poolNameTag);

    // --- ActivitySource span helpers --------------------------------------------------
    // Each returns Activity? — null when no listener is attached (cheap no-op fast path).
    // Tag-prep work is kept trivial (a single SetTag call) so HasListeners() guards aren't
    // needed except where preparatory work is non-trivial (e.g. StartSweepSpan, where the
    // caller is about to allocate ActivityTagsCollection per item).

    public Activity? StartAcquireSpan()
    {
        var a = ActivitySource.StartActivity("Pool.Acquire", ActivityKind.Internal);
        a?.SetTag(PoolMeterNames.PoolNameTag, _poolNameTag.Value);
        return a;
    }

    public Activity? StartReleaseSpan()
    {
        var a = ActivitySource.StartActivity("Pool.Release", ActivityKind.Internal);
        a?.SetTag(PoolMeterNames.PoolNameTag, _poolNameTag.Value);
        return a;
    }

    public Activity? StartGrowSpan(string poolName, GrowDecision decision)
    {
        var a = ActivitySource.StartActivity("Pool.Grow", ActivityKind.Internal);
        if (a is null) return null;
        a.SetTag(PoolMeterNames.PoolNameTag, poolName);
        a.SetTag("pool.size_after", decision.CurrentTotal + 1);
        a.SetTag("grow.tripped_by_waiters", decision.TrippedByWaiters);
        a.SetTag("grow.tripped_by_utilization", decision.TrippedByUtilization);
        a.SetTag("grow.tripped_by_p95", decision.TrippedByP95);
        return a;
    }

    public Activity? StartShrinkSpan(string poolName, int oldTotal, int newTotal)
    {
        var a = ActivitySource.StartActivity("Pool.Shrink", ActivityKind.Internal);
        if (a is null) return null;
        a.SetTag(PoolMeterNames.PoolNameTag, poolName);
        a.SetTag("pool.size_before", oldTotal);
        a.SetTag("pool.size_after", newTotal);
        return a;
    }

    public Activity? StartSweepSpan(string poolName)
    {
        // Per RESEARCH §"Pattern 6": this is the one site where preparatory work
        // for per-item ActivityEvents is non-trivial — explicit HasListeners() guard.
        if (!ActivitySource.HasListeners()) return null;
        var a = ActivitySource.StartActivity("Pool.Sweep", ActivityKind.Internal);
        a?.SetTag(PoolMeterNames.PoolNameTag, poolName);
        return a;
    }

    public Activity? StartHealthCheckSpan(string poolName)
    {
        // WR-04 fix: invoked once per idle item inside the sweep health-check loop (O(n) per tick).
        // Apply the same HasListeners() guard as StartSweepSpan so when no OTel listener is
        // attached we short-circuit before paying the StartActivity listener-walk cost.
        if (!ActivitySource.HasListeners()) return null;
        var a = ActivitySource.StartActivity("Pool.HealthCheck", ActivityKind.Internal);
        a?.SetTag(PoolMeterNames.PoolNameTag, poolName);
        return a;
    }

    public void Dispose()
    {
        if (_ownsMeter) _meter.Dispose();
    }
}

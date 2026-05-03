namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// Outcome of a <see cref="PressureSampler{T}.Evaluate"/> call. All three signals are computed (no
/// short-circuit) so telemetry can attribute which signal tripped grow.
/// </summary>
internal readonly record struct GrowDecision(
    bool ShouldGrow,
    int CurrentTotal,
    bool TrippedByWaiters,
    bool TrippedByUtilization,
    bool TrippedByP95);

/// <summary>
/// Composite-signal grow evaluator. ShouldGrow is the OR of:
/// (1) parked waiters >= GrowOnWaiterCount, (2) avg utilization >= GrowOnUtilizationPercent,
/// (3) acquire-wait p95 >= GrowOnWaitTimeP95. Caps at MaxSize.
/// </summary>
internal sealed class PressureSampler<T> where T : notnull
{
    private readonly Builder.AdaptivePoolOptions<T> _options;
    private readonly UtilizationSampler _util;
    private readonly WaitDurationHistogram _wait;

    public PressureSampler(Builder.AdaptivePoolOptions<T> options, UtilizationSampler util, WaitDurationHistogram wait)
    {
        _options = options;
        _util = util;
        _wait = wait;
    }

    public WaitDurationHistogram WaitHistogram => _wait;
    public UtilizationSampler Utilization => _util;

    public GrowDecision Evaluate(int currentTotal, int currentWaiters)
    {
        if (currentTotal >= _options.MaxSize)
            return new GrowDecision(false, currentTotal, false, false, false);
        var byWaiters = currentWaiters >= _options.GrowOnWaiterCount;
        var byUtilization = _util.AverageUtilization() >= _options.GrowOnUtilizationPercent;
        var byP95 = _wait.P95 >= _options.GrowOnWaitTimeP95;
        return new GrowDecision(byWaiters | byUtilization | byP95, currentTotal, byWaiters, byUtilization, byP95);
    }
}

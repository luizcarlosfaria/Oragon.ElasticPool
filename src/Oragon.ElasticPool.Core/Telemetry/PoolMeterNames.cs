namespace Oragon.ElasticPool.Core.Telemetry;

/// <summary>
/// Instrument-name and tag-key constants for the pool's telemetry surface. Centralized so
/// Meter, ActivitySource, and tag-key spellings stay in lock-step across the codebase.
/// </summary>
internal static class PoolMeterNames
{
    // Phase 1 names ----------------------------------------------------------------------
    public const string MeterName = "Oragon.ElasticPool";
    public const string AcquireCount = "pool.acquire.count";
    public const string FactoryFailures = "pool.factory.failures";
    public const string PoolNameTag = "pool.name";

    // Observable gauges.
    public const string Size = "pool.size";
    public const string Available = "pool.available";
    public const string InUse = "pool.in_use";
    public const string Waiting = "pool.waiting";

    // Phase 2 — telemetry expansion (CONTEXT D-09..D-13). The ActivitySource name is the same
    // string as MeterName but they are different types (one is a Meter, one is an ActivitySource);
    // OTel pipelines wire them separately. Keeping the constant separate avoids ambiguity.
    public const string ActivitySourceName = "Oragon.ElasticPool";

    // Bounded-cardinality outcome tag (CONTEXT decision: grew/shrunk/healthy/unhealthy/skipped/ok/canceled).
    public const string OutcomeTag = "outcome";

    // Phase 2 counters.
    public const string GrowCount = "pool.grow.count";
    public const string ShrinkCount = "pool.shrink.count";
    public const string HealthFailures = "pool.health.failures";

    // Phase 2 histograms — both in seconds per OTel convention (RESEARCH OQ #2).
    public const string AcquireWaitDuration = "pool.acquire.wait.duration";
    public const string SweepDuration = "pool.sweep.duration";
}

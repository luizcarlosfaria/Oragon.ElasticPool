using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Hooks;
using Oragon.ElasticPool.Core.Internals;
using Oragon.ElasticPool.Core.Policies;

namespace Oragon.ElasticPool.Core.Builder;

public sealed class ElasticPoolBuilder<T> where T : notnull
{
    private readonly IServiceProvider _services;
    private readonly CancellationToken _ct;
    private FactoryDelegate<T>? _factory;
    private BeforeUseDelegate<T>? _beforeUse;
    private CheckDelegate<T>? _check;
    private AfterUseDelegate<T>? _afterUse;
    private ReleaseDelegate<T>? _release;
    private int _minSize = 0;
    private int _maxSize = 1;
    private int _initialSize = 0;
    private WaitBehavior _whenExhausted = WaitBehavior.Wait;
    private IItemFailurePolicy<T>? _failurePolicy;
    private TimeProvider _timeProvider = TimeProvider.System;
    private string _name = string.Empty;

    // Phase 2 tunables — defaults locked per CONTEXT.md D-01..D-08.
    private int _growOnWaiterCount = 1;
    private double _growOnUtilizationPercent = 0.80;
    private TimeSpan _growOnWaitTimeP95 = TimeSpan.FromMilliseconds(100);
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(60);
    private double _shrinkOnUtilizationPercent = 0.50;
    private double _shrinkTargetUtilizationPercent = 0.75;
    private int _shrinkBatchSize = 1;
    private int _shrinkCooldownWindows = 3;
    private TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
    private TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);
    private int? _maxWaiterCount;

    internal ElasticPoolBuilder(IServiceProvider services, CancellationToken ct)
    { _services = services; _ct = ct; }

    public ElasticPoolBuilder<T> Factory(FactoryDelegate<T> factory)
    { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); return this; }
    public ElasticPoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook)
    { _beforeUse = hook ?? throw new ArgumentNullException(nameof(hook)); return this; }
    /// <summary>
    /// Registers a background health-check hook invoked by the sweeper for idle entries.
    /// Use <see cref="BeforeUse"/> for cheap on-borrow validation.
    /// </summary>
    public ElasticPoolBuilder<T> Check(CheckDelegate<T> hook)
    { _check = hook ?? throw new ArgumentNullException(nameof(hook)); return this; }
    public ElasticPoolBuilder<T> AfterUse(AfterUseDelegate<T> hook)
    { _afterUse = hook ?? throw new ArgumentNullException(nameof(hook)); return this; }
    public ElasticPoolBuilder<T> Release(ReleaseDelegate<T> hook)
    { _release = hook ?? throw new ArgumentNullException(nameof(hook)); return this; }

    /// <summary>
    /// Synchronous overload of <see cref="Factory(FactoryDelegate{T})"/>. The delegate's result
    /// is wrapped in a completed <see cref="ValueTask{T}"/> internally — zero allocation on the
    /// fast path. Use this when constructing the pooled instance is purely in-memory.
    /// </summary>
    public ElasticPoolBuilder<T> Factory(FactorySyncDelegate<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (sp, ct) => new ValueTask<T>(factory(sp, ct));
        return this;
    }

    /// <summary>
    /// Synchronous overload of <see cref="BeforeUse(BeforeUseDelegate{T})"/>. The delegate's result
    /// is wrapped in a completed <see cref="ValueTask{PoolState}"/> internally.
    /// </summary>
    public ElasticPoolBuilder<T> BeforeUse(BeforeUseSyncDelegate<T> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _beforeUse = (item, ct) => new ValueTask<PoolState>(hook(item, ct));
        return this;
    }

    /// <summary>
    /// Synchronous overload of <see cref="Check(CheckDelegate{T})"/>. The delegate's result
    /// is wrapped in a completed <see cref="ValueTask{PoolState}"/> internally.
    /// </summary>
    public ElasticPoolBuilder<T> Check(CheckSyncDelegate<T> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _check = (item, ct) => new ValueTask<PoolState>(hook(item, ct));
        return this;
    }

    /// <summary>
    /// Synchronous overload of <see cref="AfterUse(AfterUseDelegate{T})"/>. The delegate's result
    /// is wrapped in a completed <see cref="ValueTask{PoolState}"/> internally.
    /// </summary>
    public ElasticPoolBuilder<T> AfterUse(AfterUseSyncDelegate<T> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _afterUse = (item, ct) => new ValueTask<PoolState>(hook(item, ct));
        return this;
    }

    /// <summary>
    /// Synchronous overload of <see cref="Release(ReleaseDelegate{T})"/>. The delegate is invoked
    /// synchronously and a completed <see cref="ValueTask"/> is returned to the engine.
    /// Use this when cleanup is purely synchronous (e.g., <c>IDisposable.Dispose()</c>).
    /// </summary>
    public ElasticPoolBuilder<T> Release(ReleaseSyncDelegate<T> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _release = (item, ct) => { hook(item, ct); return ValueTask.CompletedTask; };
        return this;
    }
    public ElasticPoolBuilder<T> WithBounds(int minSize, int maxSize, int initialSize) { _minSize = minSize; _maxSize = maxSize; _initialSize = initialSize; return this; }
    public ElasticPoolBuilder<T> WhenExhausted(WaitBehavior behavior) { _whenExhausted = behavior; return this; }
    public ElasticPoolBuilder<T> WithFailurePolicy(IItemFailurePolicy<T> policy)
    { _failurePolicy = policy ?? throw new ArgumentNullException(nameof(policy)); return this; }
    public ElasticPoolBuilder<T> WithTimeProvider(TimeProvider timeProvider)
    { _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); return this; }
    internal ElasticPoolBuilder<T> WithName(string name) { _name = name ?? string.Empty; return this; }

    /// <summary>
    /// Configures the parked-waiters threshold for adaptive grow. Default: 1 (any wait triggers grow).
    /// </summary>
    public ElasticPoolBuilder<T> GrowOnWaiterCount(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "GrowOnWaiterCount must be >= 1.");
        _growOnWaiterCount = n; return this;
    }

    /// <summary>
    /// Configures the sustained utilization threshold (in-use / total) for adaptive grow. Default: 0.80.
    /// </summary>
    public ElasticPoolBuilder<T> GrowOnUtilizationPercent(double p)
    {
        if (p <= 0.0 || p > 1.0) throw new ArgumentOutOfRangeException(nameof(p), "GrowOnUtilizationPercent must be in (0, 1].");
        _growOnUtilizationPercent = p; return this;
    }

    /// <summary>
    /// Configures the p95 acquire-wait threshold for adaptive grow. Default: 100 ms.
    /// </summary>
    public ElasticPoolBuilder<T> GrowOnWaitTimeP95(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t), "GrowOnWaitTimeP95 must be > TimeSpan.Zero.");
        _growOnWaitTimeP95 = t; return this;
    }

    /// <summary>
    /// Configures the idle-item discard threshold used by the background sweeper. Default: 60 seconds.
    /// </summary>
    public ElasticPoolBuilder<T> IdleTimeout(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t), "IdleTimeout must be > TimeSpan.Zero.");
        _idleTimeout = t; return this;
    }

    /// <summary>
    /// Configures the utilization threshold (in-use / total) at or below which sustained
    /// low pressure may shrink the pool. Default: 0.50.
    /// </summary>
    public ElasticPoolBuilder<T> ShrinkOnUtilizationPercent(double p)
    {
        if (p < 0.0 || p > 1.0) throw new ArgumentOutOfRangeException(nameof(p), "ShrinkOnUtilizationPercent must be in [0, 1].");
        _shrinkOnUtilizationPercent = p; return this;
    }

    /// <summary>
    /// Configures the target utilization used to compute post-shrink size. Default: 0.75.
    /// </summary>
    public ElasticPoolBuilder<T> ShrinkTargetUtilizationPercent(double p)
    {
        if (p <= 0.0 || p > 1.0) throw new ArgumentOutOfRangeException(nameof(p), "ShrinkTargetUtilizationPercent must be in (0, 1].");
        _shrinkTargetUtilizationPercent = p; return this;
    }

    /// <summary>
    /// Configures the maximum number of available items evicted per shrink tick. Default: 1.
    /// </summary>
    public ElasticPoolBuilder<T> ShrinkBatchSize(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "ShrinkBatchSize must be >= 1.");
        _shrinkBatchSize = n; return this;
    }

    /// <summary>
    /// Configures the number of sweep windows after a grow during which shrink is suppressed (hysteresis). Default: 3.
    /// </summary>
    public ElasticPoolBuilder<T> ShrinkCooldownWindows(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "ShrinkCooldownWindows must be >= 0.");
        _shrinkCooldownWindows = n; return this;
    }

    /// <summary>
    /// Configures the background sweep tick interval. Default: 30 seconds.
    /// </summary>
    public ElasticPoolBuilder<T> SweepInterval(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t), "SweepInterval must be > TimeSpan.Zero.");
        _sweepInterval = t; return this;
    }

    /// <summary>
    /// Configures the maximum sweep interval after exponential backoff on consecutive failure windows. Default: 5 minutes.
    /// </summary>
    public ElasticPoolBuilder<T> MaxBackoff(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t), "MaxBackoff must be > TimeSpan.Zero.");
        _maxBackoff = t; return this;
    }

    /// <summary>
    /// Configures the maximum number of parked <c>AcquireAsync</c> waiters.
    /// Default is unbounded. Use 0 to reject instead of parking when the pool is exhausted.
    /// </summary>
    public ElasticPoolBuilder<T> MaxWaiterCount(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "MaxWaiterCount must be >= 0.");
        _maxWaiterCount = n; return this;
    }

    public IElasticPool<T> Build()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory hook is required. Call .Factory(...) before .Build().");
        if (_minSize < 0)
            throw new ArgumentOutOfRangeException(nameof(_minSize), "MinSize must be >= 0.");
        if (_maxSize < 1)
            throw new ArgumentOutOfRangeException(nameof(_maxSize), "MaxSize must be >= 1.");
        if (_minSize > _maxSize)
            throw new InvalidOperationException($"MinSize ({_minSize}) cannot exceed MaxSize ({_maxSize}).");
        if (_initialSize < _minSize || _initialSize > _maxSize)
            throw new InvalidOperationException(
                $"InitialSize ({_initialSize}) must satisfy MinSize ({_minSize}) <= InitialSize <= MaxSize ({_maxSize}).");
        if (_growOnWaiterCount > _maxSize)
            throw new InvalidOperationException(
                $"GrowOnWaiterCount ({_growOnWaiterCount}) must not exceed MaxSize ({_maxSize}).");
        if (_sweepInterval > _maxBackoff)
            throw new InvalidOperationException(
                $"SweepInterval ({_sweepInterval}) must not exceed MaxBackoff ({_maxBackoff}).");

        var options = new ElasticPoolOptions<T>
        {
            Factory = _factory,
            BeforeUse = _beforeUse,
            Check = _check,
            AfterUse = _afterUse,
            Release = _release,
            MinSize = _minSize,
            MaxSize = _maxSize,
            InitialSize = _initialSize,
            WhenExhausted = _whenExhausted,
            FailurePolicy = _failurePolicy ?? new DiscardAndReplaceFailurePolicy<T>(),
            TimeProvider = _timeProvider,
            PoolName = _name,
            GrowOnWaiterCount = _growOnWaiterCount,
            GrowOnUtilizationPercent = _growOnUtilizationPercent,
            GrowOnWaitTimeP95 = _growOnWaitTimeP95,
            IdleTimeout = _idleTimeout,
            ShrinkOnUtilizationPercent = _shrinkOnUtilizationPercent,
            ShrinkTargetUtilizationPercent = _shrinkTargetUtilizationPercent,
            ShrinkBatchSize = _shrinkBatchSize,
            ShrinkCooldownWindows = _shrinkCooldownWindows,
            SweepInterval = _sweepInterval,
            MaxBackoff = _maxBackoff,
            MaxWaiterCount = _maxWaiterCount,
        };

        return new ElasticPool<T>(options, _services, _ct);
    }
}

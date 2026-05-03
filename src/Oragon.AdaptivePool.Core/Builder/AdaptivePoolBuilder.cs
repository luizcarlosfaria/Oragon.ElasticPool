using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Hooks;
using Oragon.AdaptivePool.Core.Internals;
using Oragon.AdaptivePool.Core.Policies;

namespace Oragon.AdaptivePool.Core.Builder;

public sealed class AdaptivePoolBuilder<T> where T : notnull
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

    internal AdaptivePoolBuilder(IServiceProvider services, CancellationToken ct)
    { _services = services; _ct = ct; }

    public AdaptivePoolBuilder<T> Factory(FactoryDelegate<T> factory)
    { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); return this; }
    public AdaptivePoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook) { _beforeUse = hook; return this; }
    public AdaptivePoolBuilder<T> Check(CheckDelegate<T> hook) { _check = hook; return this; }
    public AdaptivePoolBuilder<T> AfterUse(AfterUseDelegate<T> hook) { _afterUse = hook; return this; }
    public AdaptivePoolBuilder<T> Release(ReleaseDelegate<T> hook) { _release = hook; return this; }
    public AdaptivePoolBuilder<T> WithBounds(int minSize, int maxSize, int initialSize)
    { _minSize = minSize; _maxSize = maxSize; _initialSize = initialSize; return this; }
    public AdaptivePoolBuilder<T> WhenExhausted(WaitBehavior behavior) { _whenExhausted = behavior; return this; }
    public AdaptivePoolBuilder<T> WithFailurePolicy(IItemFailurePolicy<T> policy)
    { _failurePolicy = policy ?? throw new ArgumentNullException(nameof(policy)); return this; }
    public AdaptivePoolBuilder<T> WithTimeProvider(TimeProvider timeProvider)
    { _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); return this; }
    internal AdaptivePoolBuilder<T> WithName(string name) { _name = name ?? string.Empty; return this; }

    public IAdaptivePool<T> Build()
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

        var options = new AdaptivePoolOptions<T>
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
        };

        return new AdaptivePool<T>(options, _services, _ct);
    }
}

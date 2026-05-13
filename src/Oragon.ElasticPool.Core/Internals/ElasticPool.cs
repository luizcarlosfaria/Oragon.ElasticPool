using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.Exceptions;
using Oragon.ElasticPool.Core.Telemetry;

namespace Oragon.ElasticPool.Core.Internals;

internal sealed class ElasticPool<T> : IElasticPool<T>
    where T : notnull
{
    private readonly ElasticPoolOptions<T> _options;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<PoolEntry<T>> _idle = new();
    // CR-03 fix: discard set for entries the sweep loop has flagged Unhealthy. Checked on every
    // path that dequeues from _idle so consumers never receive an entry the engine has already
    // judged broken — independently of the shrink cooldown gate, which only governs SIZE-based
    // eviction. ConcurrentDictionary used as a thread-safe HashSet (the value byte is unused).
    // PoolEntry<T> is a sealed class with default reference equality — sufficient for set semantics.
    private readonly ConcurrentDictionary<PoolEntry<T>, byte> _pendingDiscard = new();
    // Direct-handoff waiter queue per RESEARCH Pitfall 1.
    private readonly Channel<TaskCompletionSource<PoolEntry<T>>> _waiters;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly TelemetryEmitter _telemetry;
    private readonly ILogger _log;

    private int _total;       // total live items (idle + in-use + being-created)
    private int _inUse;       // items currently checked out
    private int _lifecycle = (int)PoolLifecycle.Open;

    // Phase 2 elasticity wiring (instantiated in ctor; behavior added in Plan 02 / Plan 03).
    private readonly UtilizationSampler _utilSampler;
    private readonly WaitDurationHistogram _waitHistogram;
    private readonly PressureSampler<T> _pressure;
    private readonly SweepBackoffState _backoffState;
    private readonly BackgroundSweeper<T> _sweeper;
    private int _waitersCount;          // tracked alongside _total/_inUse for PressureSampler.
    private int _sinceLastGrowTicks;    // hysteresis cooldown counter (Plan 02 reads/resets).

    public Task WarmupTask { get; }

    public int MaxSize => _options.MaxSize;
    public int MinSize => _options.MinSize;
    public int Total => Volatile.Read(ref _total);
    public int Available => _idle.Count;
    public int InUse => Volatile.Read(ref _inUse);
    public int Waiting => Volatile.Read(ref _waitersCount);

    internal ElasticPool(ElasticPoolOptions<T> options, IServiceProvider services, CancellationToken ct)
    {
        _options = options;
        _services = services;
        _time = options.TimeProvider;
        _waiters = Channel.CreateUnbounded<TaskCompletionSource<PoolEntry<T>>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _telemetry = new TelemetryEmitter(
            services,
            options.PoolName,
            () => Volatile.Read(ref _total),
            () => _idle.Count,
            () => Volatile.Read(ref _inUse),
            () => Volatile.Read(ref _waitersCount));

        // Best-effort logger; if Logging is not registered, use NullLogger.
        var loggerFactory = services.GetService<ILoggerFactory>();
        _log = loggerFactory?.CreateLogger($"Oragon.ElasticPool.{typeof(T).Name}") ?? NullLogger.Instance;

        // Phase 2 wiring — components instantiated; sweep loop starts immediately.
        _utilSampler = new UtilizationSampler(_time, options.UtilizationWindow, TimeSpan.FromSeconds(1));
        _waitHistogram = new WaitDurationHistogram();
        _pressure = new PressureSampler<T>(options, _utilSampler, _waitHistogram);
        _backoffState = new SweepBackoffState(options.SweepInterval, options.MaxBackoff);
        _sweeper = new BackgroundSweeper<T>(this, options, _backoffState, _lifetimeCts.Token);

        WarmupTask = WarmupAsync(_lifetimeCts.Token);
    }

    // --- Internal probes consumed by BackgroundSweeper (Plan 01 stub) and Plan 02 grow/shrink. ---
    internal void IncrementSinceLastGrowTicks() => Interlocked.Increment(ref _sinceLastGrowTicks);
    internal int SinceLastGrowTicks => Volatile.Read(ref _sinceLastGrowTicks);
    internal void ResetSinceLastGrowTicks() => Interlocked.Exchange(ref _sinceLastGrowTicks, 0);
    internal int CurrentTotal => Volatile.Read(ref _total);
    internal int WaitersCount => Volatile.Read(ref _waitersCount);
    internal ConcurrentQueue<PoolEntry<T>> Idle => _idle;
    /// <summary>CR-03: sweeper marks Check-Unhealthy entries here so dequeue paths skip them.</summary>
    internal ConcurrentDictionary<PoolEntry<T>, byte> PendingDiscard => _pendingDiscard;
    internal Channel<TaskCompletionSource<PoolEntry<T>>> Waiters => _waiters;
    internal ElasticPoolOptions<T> Options => _options;
    internal PressureSampler<T> Pressure => _pressure;
    internal UtilizationSampler UtilSampler => _utilSampler;
    internal WaitDurationHistogram WaitHistogram => _waitHistogram;
    internal SweepBackoffState BackoffState => _backoffState;
    internal BackgroundSweeper<T> Sweeper => _sweeper;

    // Plan 02 — Telemetry/Log accessors for the BackgroundSweeper shrink/health-check pass.
    internal TelemetryEmitter Telemetry => _telemetry;
    internal ILogger Log => _log;
    /// <summary>Sweeper-only helper: decrements the live-item counter by one (shrink pass owns dequeue+decrement).</summary>
    internal void DecrementTotal() => Interlocked.Decrement(ref _total);

    private bool TryReserveWaiterSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _waitersCount);
            if (_options.MaxWaiterCount is int maxWaiters && current >= maxWaiters)
                return false;
            if (Interlocked.CompareExchange(ref _waitersCount, current + 1, current) == current)
                return true;
        }
    }

    private void ReleaseWaiterSlot() => Interlocked.Decrement(ref _waitersCount);

    /// <summary>
    /// CR-03 fix: dequeue from idle, transparently skipping entries the sweep has flagged as
    /// pending-discard. For each pending-discard hit: remove from the discard set, decrement
    /// _total, fire-and-forget Release, and retry. Returns true if a healthy entry was acquired.
    /// </summary>
    internal bool TryDequeueIdle(out PoolEntry<T> entry)
    {
        while (_idle.TryDequeue(out var candidate))
        {
            if (_pendingDiscard.TryRemove(candidate, out _))
            {
                // Sweeper flagged this as Unhealthy; never serve it to a consumer.
                Interlocked.Decrement(ref _total);
                TryReleaseFireAndForget(candidate);
                continue;
            }
            entry = candidate;
            return true;
        }
        entry = null!;
        return false;
    }

    public Task ReadyAsync() => WarmupTask;

    private async Task WarmupAsync(CancellationToken ct)
    {
        if (_options.InitialSize == 0) return;

        var tasks = new Task[_options.InitialSize];
        for (int i = 0; i < _options.InitialSize; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                Interlocked.Increment(ref _total);
                try
                {
                    var item = await _options.Factory(_services, ct).ConfigureAwait(false);
                    var now = _time.GetUtcNow();
                    _idle.Enqueue(new PoolEntry<T>(item, now, now));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref _total);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _total);
                    _telemetry.OnFactoryFailure();
                    _log.FactoryFailed(_options.PoolName, ex);
                    throw;
                }
            }, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public IPoolItem<T> Acquire()
    {
        ThrowIfDisposed();
        // CR-04 fix: sync Acquire MUST honor BeforeUse so it has the same health guarantees
        // as AcquireAsync. BeforeUse is contractually cheap (<1ms p99 per HookDelegates docs),
        // so blocking on it is acceptable. We block via GetAwaiter().GetResult() (TPL pattern
        // for sync-over-async on cheap hooks). On Unhealthy, recurse to AcquireAsync sync-blocked
        // so the discard+replace path runs identically; if that path needs to wait/grow it will
        // throw (sync NEVER blocks per CONTEXT.md + RESEARCH OQ3) — except inside the bounded
        // BeforeUse-Unhealthy retry loop where it grows on-demand without waiting.
        while (TryDequeueIdle(out var entry))
        {
            if (_options.BeforeUse is { } beforeUse)
            {
                PoolState state;
                try
                {
                    // Sync-over-async on a hook contractually < 1ms p99. AsTask().GetAwaiter().GetResult()
                    // unwraps AggregateException so the original hook exception is observed if BeforeUse throws.
                    state = beforeUse(entry.Item, _lifetimeCts.Token).AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    state = PoolState.Unhealthy;
                }
                if (state == PoolState.Unhealthy)
                {
                    _log.BeforeUseUnhealthy(_options.PoolName);
                    Interlocked.Decrement(ref _total);
                    try
                    {
                        _options.FailurePolicy.HandleAsync(entry.Item, FailureKind.BeforeUseUnhealthy, null, _lifetimeCts.Token)
                            .AsTask().GetAwaiter().GetResult();
                    }
                    catch { /* policy failure — continue */ }
                    if (_options.Release is { } release)
                    {
                        try { release(entry.Item, _lifetimeCts.Token).AsTask().GetAwaiter().GetResult(); } catch { /* swallow */ }
                    }
                    // Try the next idle entry (if any). If idle is empty, fall through to PoolExhausted —
                    // sync MUST NEVER block awaiting growth.
                    continue;
                }
            }
            Interlocked.Increment(ref _inUse);
            _telemetry.OnAcquire();
            _utilSampler.Sample(Volatile.Read(ref _inUse), Volatile.Read(ref _total));
            return new PoolItem<T>(this, entry);
        }
        // Sync NEVER blocks (per CONTEXT.md + RESEARCH Open Question 3).
        throw new PoolExhaustedException(_options.MaxSize);
    }

    public ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        // Wrap the public path in a Pool.Acquire span. The span is null when no listener is
        // attached (cheap no-op); outcome tagging happens in the helper.
        var span = _telemetry.StartAcquireSpan();
        return AcquireAsyncCoreWithSpan(span, cancellationToken, retryCount: 0);
    }

    private async ValueTask<IPoolItem<T>> AcquireAsyncCoreWithSpan(Activity? span, CancellationToken ct, int retryCount)
    {
        try
        {
            var item = await AcquireAsyncCore(ct, retryCount).ConfigureAwait(false);
            span?.SetTag(PoolMeterNames.OutcomeTag, "ok");
            return item;
        }
        catch (OperationCanceledException)
        {
            span?.SetTag(PoolMeterNames.OutcomeTag, "canceled");
            throw;
        }
        catch
        {
            // WR-02 fix: tag any non-cancellation failure (PoolExhaustedException,
            // ObjectDisposedException, BeforeUseUnhealthy retry-limit InvalidOperationException,
            // factory rethrow, etc.) with outcome="error" so OTel backends partitioning by
            // outcome capture these events instead of silently dropping them.
            span?.SetTag(PoolMeterNames.OutcomeTag, "error");
            throw;
        }
        finally { span?.Dispose(); }
    }

    private async ValueTask<IPoolItem<T>> AcquireAsyncCore(CancellationToken cancellationToken, int retryCount)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var ct = linked.Token;

        // Fast path: free item available. TryDequeueIdle skips pending-discard entries (CR-03).
        if (TryDequeueIdle(out var entry))
        {
            return await PrepareForUseAsync(entry, ct, cancellationToken, retryCount).ConfigureAwait(false);
        }

        // Phase 2 composite-signal grow gate. Replace Phase 1's unconditional CAS-grow loop with
        // a pressure consultation. The caller is counted AS-IF parked (waiters + 1) so the
        // default `GrowOnWaiterCount = 1` keeps Phase 1's semantics: any thread reaching the
        // slow path triggers grow. Higher GrowOnWaiterCount values delay grow until a real
        // queue forms (CONTEXT D-01: tolerance to spikes). MinSize-respecting clause guarantees
        // cold-start warmup still climbs to MinSize even when pressure says no-grow.
        var waiters = Volatile.Read(ref _waitersCount) + 1;
        var decision = _pressure.Evaluate(Volatile.Read(ref _total), waiters);

        if (decision.ShouldGrow || Volatile.Read(ref _total) < _options.MinSize)
        {
            var grew = await TryGrowAsync(decision, ct, cancellationToken).ConfigureAwait(false);
            if (grew is not null)
                return await PrepareForUseAsync(grew, ct, cancellationToken, retryCount).ConfigureAwait(false);
        }

        // Pool is exhausted (at MaxSize, or pressure said no-grow). Fork on WaitBehavior:
        // Throw → PoolExhaustedException synchronously; Wait → park as a direct-handoff waiter.
        if (_options.WhenExhausted == WaitBehavior.Throw)
            throw new PoolExhaustedException(_options.MaxSize);

        // WaitBehavior.Wait — direct-handoff waiter. Record wait duration into both the internal
        // histogram (used by PressureSampler.P95) and the OTel histogram on every parked wait.
        var tcs = new TaskCompletionSource<PoolEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitStart = _time.GetTimestamp();
        if (!TryReserveWaiterSlot())
            throw new PoolExhaustedException(_options.MaxSize);
        try
        {
            await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseWaiterSlot();
            var elapsedOnError = _time.GetElapsedTime(waitStart);
            _waitHistogram.Record(elapsedOnError);
            _telemetry.OnAcquireWait(elapsedOnError);
            throw;
        }

        // WR-01 fix: pass the cancellation token to TrySetCanceled so awaiters see the
        // correct CancellationToken on the resulting OperationCanceledException.
        using var registration = ct.Register(static state =>
        {
            var (t, token) = ((TaskCompletionSource<PoolEntry<T>>, CancellationToken))state!;
            t.TrySetCanceled(token);
        }, (tcs, ct));

        try
        {
            var awaited = await tcs.Task.ConfigureAwait(false);
            return await PrepareForUseAsync(awaited, ct, cancellationToken, retryCount).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Re-throw caller's CT semantics per RESEARCH Pattern 8.
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new ObjectDisposedException(nameof(ElasticPool<T>), "Pool was disposed during AcquireAsync.");
        }
        finally
        {
            ReleaseWaiterSlot();
            var elapsed = _time.GetElapsedTime(waitStart);
            _waitHistogram.Record(elapsed);
            _telemetry.OnAcquireWait(elapsed);
        }
    }

    /// <summary>
    /// CAS-reserves a slot under MaxSize, calls Factory outside any lock, resets the cooldown
    /// counter on success, emits Pool.Grow span/counter/log. Returns null when MaxSize is hit
    /// or the CAS lost — caller falls through to the wait branch.
    /// </summary>
    private async ValueTask<PoolEntry<T>?> TryGrowAsync(GrowDecision decision, CancellationToken ct, CancellationToken callerCt)
    {
        int currentTotal = Volatile.Read(ref _total);
        if (currentTotal >= _options.MaxSize) return null;
        if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) != currentTotal) return null;

        using var span = _telemetry.StartGrowSpan(_options.PoolName, decision);
        try
        {
            var newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
            var growNow = _time.GetUtcNow();
            var entry = new PoolEntry<T>(newItem, growNow, growNow);
            Interlocked.Exchange(ref _sinceLastGrowTicks, 0);
            _telemetry.OnGrow();
            _log.Grew(_options.PoolName, currentTotal, currentTotal + 1,
                decision.TrippedByWaiters, decision.TrippedByUtilization, decision.TrippedByP95);
            span?.SetTag(PoolMeterNames.OutcomeTag, "grew");
            return entry;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _total);
            span?.SetTag(PoolMeterNames.OutcomeTag, "canceled");
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _total);
            _telemetry.OnFactoryFailure();
            _log.FactoryFailed(_options.PoolName, ex);
            span?.SetTag(PoolMeterNames.OutcomeTag, "factory_failed");
            await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct).ConfigureAwait(false);
            throw;
        }
    }

    // Maximum number of consecutive BeforeUse-Unhealthy discards before we give up and
    // surface a meaningful exception. Guards against unbounded recursion when a Factory
    // always succeeds but BeforeUse always rejects (WR-04). 10 is an arbitrary but
    // sensible cap — a healthy pool should never hit this.
    private const int BeforeUseUnhealthyRetryLimit = 10;

    // CR-02 + WR-04: takes BOTH the composite ct (linked: caller + lifetime) AND the original
    // caller cancellation token. The composite is used for hook invocations (so they abort on
    // pool dispose); the caller token is used to recurse into AcquireAsyncCore so that
    // disposal-vs-cancellation semantics are preserved (caller still observes
    // ObjectDisposedException on pool dispose, not OperationCanceledException).
    // retryCount bounds the BeforeUse-Unhealthy retry loop.
    private async ValueTask<IPoolItem<T>> PrepareForUseAsync(
        PoolEntry<T> entry, CancellationToken ct, CancellationToken callerCt, int retryCount = 0)
    {
        if (_options.BeforeUse is { } beforeUse)
        {
            PoolState state;
            try
            {
                state = await beforeUse(entry.Item, ct).ConfigureAwait(false);
            }
            catch
            {
                // BeforeUse threw — treat as Unhealthy.
                state = PoolState.Unhealthy;
            }
            if (state == PoolState.Unhealthy)
            {
                _log.BeforeUseUnhealthy(_options.PoolName);
                Interlocked.Decrement(ref _total); // discarding broken item
                await _options.FailurePolicy.HandleAsync(entry.Item, FailureKind.BeforeUseUnhealthy, null, ct).ConfigureAwait(false);
                if (_options.Release is { } release)
                {
                    try { await release(entry.Item, ct).ConfigureAwait(false); } catch { /* swallow */ }
                }
                if (retryCount >= BeforeUseUnhealthyRetryLimit)
                {
                    throw new InvalidOperationException(
                        $"BeforeUse returned Unhealthy {BeforeUseUnhealthyRetryLimit} times consecutively for pool '{_options.PoolName}'; " +
                        "the Factory may be producing persistently broken items.");
                }
                // Replace by recursing the acquire path with the caller's original token
                // so ObjectDisposedException semantics (CR-02) are preserved on pool dispose.
                // retryCount is threaded through AcquireAsyncCore -> PrepareForUseAsync to
                // bound the BeforeUse-Unhealthy retry chain (WR-04).
                return await AcquireAsyncCore(callerCt, retryCount + 1).ConfigureAwait(false);
            }
        }
        Interlocked.Increment(ref _inUse);
        _telemetry.OnAcquire();
        _utilSampler.Sample(Volatile.Read(ref _inUse), Volatile.Read(ref _total));
        return new PoolItem<T>(this, entry);
    }

    // Called from PoolItem.Dispose() — synchronous return path.
    internal void ReturnSync(PoolEntry<T> entry)
    {
        Interlocked.Decrement(ref _inUse);
        entry.LastReturnedAt = _time.GetUtcNow();
        _utilSampler.Sample(Volatile.Read(ref _inUse), Volatile.Read(ref _total));
        if (Volatile.Read(ref _lifecycle) != (int)PoolLifecycle.Open)
        {
            // Pool is being disposed — invoke Release best-effort, do not requeue.
            TryReleaseFireAndForget(entry);
            return;
        }
        if (TryHandoff(entry)) return;
        _idle.Enqueue(entry);
    }

    // Called from PoolItem.DisposeAsync() — async return path (AfterUse can run async; Phase 1 default no-op).
    internal async ValueTask ReturnAsync(PoolEntry<T> entry)
    {
        using var span = _telemetry.StartReleaseSpan();
        try { await ReturnAsyncCore(entry).ConfigureAwait(false); span?.SetTag(PoolMeterNames.OutcomeTag, "ok"); }
        catch { span?.SetTag(PoolMeterNames.OutcomeTag, "canceled"); throw; }
    }

    private async ValueTask ReturnAsyncCore(PoolEntry<T> entry)
    {
        if (_options.AfterUse is { } afterUse)
        {
            try
            {
                var state = await afterUse(entry.Item, CancellationToken.None).ConfigureAwait(false);
                if (state == PoolState.Unhealthy)
                {
                    Interlocked.Decrement(ref _inUse);
                    Interlocked.Decrement(ref _total);
                    if (_options.Release is { } release)
                    {
                        try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
                    }
                    // CR-01 fix: if a waiter is parked, the slot we just freed must wake them.
                    // Without this, MaxSize=1 + AfterUse=Unhealthy deadlocks (waiter never observes the free slot).
                    // Strategy: fire a background grow-and-handoff. We try to reserve a slot under MaxSize,
                    // build a replacement, and hand it off to the parked waiter via TryHandoff. If no waiter
                    // is parked by the time we have the entry, enqueue it to idle. We use the lifetime token
                    // so we abort on pool dispose.
                    if (Volatile.Read(ref _lifecycle) == (int)PoolLifecycle.Open && _waiters.Reader.TryPeek(out _))
                    {
                        _ = Task.Run(() => GrowAndHandoffAsync(_lifetimeCts.Token));
                    }
                    return;
                }
            }
            catch { /* AfterUse failure → still return defensively */ }
        }
        ReturnSync(entry);
    }

    // Called from PoolItem finalizer — sync only, never throws, no Release hook (per PITFALLS Pitfall 4).
    internal void ReturnFromFinalizer(PoolEntry<T> entry)
    {
        _log.ItemLeaked(_options.PoolName);
        ReturnSync(entry);
    }

    private bool TryHandoff(PoolEntry<T> entry)
    {
        // Try to wake exactly one waiter.
        while (_waiters.Reader.TryRead(out var tcs))
        {
            if (tcs.TrySetResult(entry)) return true;
            // Waiter was canceled — drop it, try next.
        }
        return false;
    }

    // CR-01 helper: invoked after an AfterUse-Unhealthy discard to wake any parked waiter.
    // Reserves a slot under MaxSize, builds a replacement via Factory, and hands off to a waiter
    // (or enqueues to idle if no waiter is present). Errors are swallowed; the parked waiter's
    // own CancellationToken protects it from indefinite hang if growth fails.
    private async Task GrowAndHandoffAsync(CancellationToken ct)
    {
        try
        {
            int currentTotal = Volatile.Read(ref _total);
            if (currentTotal >= _options.MaxSize) return;
            if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) != currentTotal)
                return; // CAS lost — another path will handle growth.

            T newItem;
            try
            {
                newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _total);
                if (ex is not OperationCanceledException)
                {
                    _telemetry.OnFactoryFailure();
                    _log.FactoryFailed(_options.PoolName, ex);
                }
                return;
            }

            var growNow = _time.GetUtcNow();
            var entry = new PoolEntry<T>(newItem, growNow, growNow);
            // Replacement-grow after AfterUse=Unhealthy. Tag all trip-flags false (this is not
            // a pressure-driven grow). Counter accuracy: every PoolEntry creation goes through
            // OnGrow / Grew so dashboards see the true grow rate.
            _telemetry.OnGrow();
            _log.Grew(_options.PoolName, currentTotal, currentTotal + 1, false, false, false);
            if (TryHandoff(entry)) return;

            // No waiter consumed it — return to idle (it'll be served on next acquire).
            if (Volatile.Read(ref _lifecycle) == (int)PoolLifecycle.Open)
            {
                _idle.Enqueue(entry);
            }
            else
            {
                // Pool was disposed mid-growth — best-effort release.
                Interlocked.Decrement(ref _total);
                TryReleaseFireAndForget(entry);
            }
        }
        catch { /* defensive — never let a background task escape */ }
    }

    private void TryReleaseFireAndForget(PoolEntry<T> entry)
    {
        if (_options.Release is { } release)
        {
            _ = Task.Run(async () =>
            {
                try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.ReleaseHookFailedDuringDispose(_options.PoolName, ex); }
            });
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _lifecycle) != (int)PoolLifecycle.Open)
            throw new ObjectDisposedException(nameof(ElasticPool<T>));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycle, (int)PoolLifecycle.Closed) == (int)PoolLifecycle.Closed)
            return;

        // Cancel pending waiters and warm-up.
        try { _lifetimeCts.Cancel(); } catch { /* swallow */ }

        // Phase 2: stop the sweep loop BEFORE we drain. The sweeper must shut down before drain
        // so it cannot race with `_idle.TryDequeue` in the drain path below.
        try { await _sweeper.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }

        // Complete waiter channel so any in-flight WriteAsync throws.
        _waiters.Writer.TryComplete();

        // Drain remaining waiters → cancel them.
        while (_waiters.Reader.TryRead(out var tcs))
        {
            tcs.TrySetCanceled();
        }

        // Drain idle queue → invoke Release on each.
        while (_idle.TryDequeue(out var entry))
        {
            if (_options.Release is { } release)
            {
                try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.ReleaseHookFailedDuringDispose(_options.PoolName, ex); }
            }
        }

        try { _lifetimeCts.Dispose(); } catch { /* swallow */ }
        _telemetry.Dispose();
        // WR-02: no finalizer on ElasticPool<T>, so GC.SuppressFinalize is unnecessary.
    }

    public void Dispose()
    {
        // Standard non-host DI may only call Dispose. Block-with-fallback per PITFALLS Pitfall 8.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

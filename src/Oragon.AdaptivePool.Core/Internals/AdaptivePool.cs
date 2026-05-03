using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Exceptions;
using Oragon.AdaptivePool.Core.Telemetry;

namespace Oragon.AdaptivePool.Core.Internals;

internal sealed class AdaptivePool<T> : IAdaptivePool<T>
    where T : notnull
{
    private readonly AdaptivePoolOptions<T> _options;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<PoolEntry<T>> _idle = new();
    // Direct-handoff waiter queue per RESEARCH Pitfall 1.
    private readonly Channel<TaskCompletionSource<PoolEntry<T>>> _waiters;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly TelemetryEmitter _telemetry;
    private readonly ILogger _log;

    private int _total;       // total live items (idle + in-use + being-created)
    private int _inUse;       // items currently checked out
    private int _lifecycle = (int)PoolLifecycle.Open;

    public Task WarmupTask { get; }

    public int MaxSize => _options.MaxSize;
    public int MinSize => _options.MinSize;
    public int Available => _idle.Count;
    public int InUse => Volatile.Read(ref _inUse);

    internal AdaptivePool(AdaptivePoolOptions<T> options, IServiceProvider services, CancellationToken ct)
    {
        _options = options;
        _services = services;
        _time = options.TimeProvider;
        _waiters = Channel.CreateUnbounded<TaskCompletionSource<PoolEntry<T>>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _telemetry = new TelemetryEmitter(services, options.PoolName);

        // Best-effort logger; if Logging is not registered, use NullLogger.
        var loggerFactory = services.GetService<ILoggerFactory>();
        _log = loggerFactory?.CreateLogger($"Oragon.AdaptivePool.{typeof(T).Name}") ?? NullLogger.Instance;

        WarmupTask = WarmupAsync(_lifetimeCts.Token);
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
                    _idle.Enqueue(new PoolEntry<T>(item, _time.GetUtcNow()));
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
        while (_idle.TryDequeue(out var entry))
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
            return new PoolItem<T>(this, entry);
        }
        // Sync NEVER blocks (per CONTEXT.md + RESEARCH Open Question 3).
        throw new PoolExhaustedException(_options.MaxSize);
    }

    public ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
        => AcquireAsyncCore(cancellationToken, retryCount: 0);

    private async ValueTask<IPoolItem<T>> AcquireAsyncCore(CancellationToken cancellationToken, int retryCount)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var ct = linked.Token;

        // Fast path: free item available.
        if (_idle.TryDequeue(out var entry))
        {
            return await PrepareForUseAsync(entry, ct, cancellationToken, retryCount).ConfigureAwait(false);
        }

        // Try to grow up to MaxSize (Phase 1 = on-demand creation up to MaxSize, no elastic signal).
        while (true)
        {
            int currentTotal = Volatile.Read(ref _total);
            if (currentTotal >= _options.MaxSize) break;
            if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) == currentTotal)
            {
                // Reservation succeeded. Build new item OUTSIDE any lock per HOOK-01.
                T newItem;
                try
                {
                    newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Decrement(ref _total); // counter rollback per PITFALLS Pitfall 3
                    _telemetry.OnFactoryFailure();
                    _log.FactoryFailed(_options.PoolName, ex);
                    await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct).ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Decrement(ref _total);
                    throw;
                }
                var fresh = new PoolEntry<T>(newItem, _time.GetUtcNow());
                return await PrepareForUseAsync(fresh, ct, cancellationToken, retryCount).ConfigureAwait(false);
            }
            // CAS failed → retry (another thread created an item or grew).
        }

        // Pool is at MaxSize and exhausted.
        if (_options.WhenExhausted == WaitBehavior.Throw)
            throw new PoolExhaustedException(_options.MaxSize);

        // WaitBehavior.Wait — direct-handoff waiter.
        var tcs = new TaskCompletionSource<PoolEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);

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
            throw new ObjectDisposedException(nameof(AdaptivePool<T>), "Pool was disposed during AcquireAsync.");
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
        return new PoolItem<T>(this, entry);
    }

    // Called from PoolItem.Dispose() — synchronous return path.
    internal void ReturnSync(PoolEntry<T> entry)
    {
        Interlocked.Decrement(ref _inUse);
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

            var entry = new PoolEntry<T>(newItem, _time.GetUtcNow());
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
            throw new ObjectDisposedException(nameof(AdaptivePool<T>));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycle, (int)PoolLifecycle.Closed) == (int)PoolLifecycle.Closed)
            return;

        // Cancel pending waiters and warm-up.
        try { _lifetimeCts.Cancel(); } catch { /* swallow */ }

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
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        // Standard non-host DI may only call Dispose. Block-with-fallback per PITFALLS Pitfall 8.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

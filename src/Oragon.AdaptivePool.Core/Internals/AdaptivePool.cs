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
        if (_idle.TryDequeue(out var entry))
        {
            Interlocked.Increment(ref _inUse);
            _telemetry.OnAcquire();
            return new PoolItem<T>(this, entry);
        }
        // Sync NEVER blocks (per CONTEXT.md + RESEARCH Open Question 3).
        throw new PoolExhaustedException(_options.MaxSize);
    }

    public async ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var ct = linked.Token;

        // Fast path: free item available.
        if (_idle.TryDequeue(out var entry))
        {
            return await PrepareForUseAsync(entry, ct).ConfigureAwait(false);
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
                return await PrepareForUseAsync(fresh, ct).ConfigureAwait(false);
            }
            // CAS failed → retry (another thread created an item or grew).
        }

        // Pool is at MaxSize and exhausted.
        if (_options.WhenExhausted == WaitBehavior.Throw)
            throw new PoolExhaustedException(_options.MaxSize);

        // WaitBehavior.Wait — direct-handoff waiter.
        var tcs = new TaskCompletionSource<PoolEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);

        using var registration = ct.Register(static state =>
        {
            var t = (TaskCompletionSource<PoolEntry<T>>)state!;
            t.TrySetCanceled();
        }, tcs);

        try
        {
            var awaited = await tcs.Task.ConfigureAwait(false);
            return await PrepareForUseAsync(awaited, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Re-throw caller's CT semantics per RESEARCH Pattern 8.
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new ObjectDisposedException(nameof(AdaptivePool<T>), "Pool was disposed during AcquireAsync.");
        }
    }

    private async ValueTask<IPoolItem<T>> PrepareForUseAsync(PoolEntry<T> entry, CancellationToken ct)
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
                // Replace by recursing the acquire path (will create a new item up to MaxSize).
                return await AcquireAsync(ct).ConfigureAwait(false);
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

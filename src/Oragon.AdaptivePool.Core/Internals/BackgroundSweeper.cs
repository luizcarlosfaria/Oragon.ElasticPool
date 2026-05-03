using System.Diagnostics;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Telemetry;

namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// PeriodicTimer-driven background sweeper. Runs Check hook on idle items, applies failure
/// policy on Unhealthy decisions, evicts at most one idle item per tick when the pool is over
/// MinSize and grow cooldown has elapsed, and adapts the timer interval via SweepBackoffState.
/// </summary>
internal sealed class BackgroundSweeper<T> : IAsyncDisposable where T : notnull
{
    private readonly AdaptivePool<T> _pool;
    private readonly Builder.AdaptivePoolOptions<T> _options;
    private readonly SweepBackoffState _backoff;
    private readonly CancellationTokenSource _sweepCts;
    private readonly Task _sweepTask;

    // Test-only probe; resets each tick. NOT exposed via PublicAPI — internal-only signal for
    // Plan 03 tests via [InternalsVisibleTo].
    private TaskCompletionSource _tickCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task TickCompleted => _tickCompleted.Task;
    internal long TickCount; // diagnostic counter; Volatile.Read in tests

    public BackgroundSweeper(AdaptivePool<T> pool, Builder.AdaptivePoolOptions<T> options, SweepBackoffState backoff, CancellationToken lifetimeToken)
    {
        _pool = pool;
        _options = options;
        _backoff = backoff;
        _sweepCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _sweepTask = Task.Run(SweepLoopAsync);
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_backoff.CurrentInterval, _options.TimeProvider);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                var prevTcs = Interlocked.Exchange(
                    ref _tickCompleted,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                try
                {
                    await RunSweepTickAsync(timer, _sweepCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    prevTcs.TrySetResult();
                    break;
                }
                catch (Exception ex)
                {
                    // Catastrophic failure outside per-item try/catch — surface via 1099 SweepFailed
                    // and continue. The loop must not die from a single tick.
                    _pool.Log.SweepFailed(_options.PoolName, ex);
                }
                finally
                {
                    Interlocked.Increment(ref TickCount);
                    prevTcs.TrySetResult();
                }
                if (_backoff.IntervalChanged) timer.Period = _backoff.CurrentInterval;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
    }

    /// <summary>
    /// One sweep tick: health-check pass + shrink pass + cooldown bookkeeping + backoff update.
    /// Sequence: (1) start sweep span + log SweepStarted, (2) snapshot idle queue,
    /// (3) per-item Check + ActivityEvent, (4) shrink at most 1 if cooldown elapsed and over MinSize,
    /// (5) IncrementSinceLastGrowTicks, (6) OnSweepResult + log SweepFailureBackoff if interval bumped,
    /// (7) record sweep duration histogram + log SweepCompleted.
    /// </summary>
    private async ValueTask RunSweepTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        var sweepStart = _options.TimeProvider.GetTimestamp();
        using var sweepSpan = _pool.Telemetry.StartSweepSpan(_options.PoolName);
        _pool.Log.SweepStarted(_options.PoolName, _backoff.CurrentInterval.TotalSeconds);

        int totalChecked = 0;
        int unhealthy = 0;
        int shrunk = 0;

        // ---- (1) Health-check pass ----
        // Iterate a snapshot — ConcurrentQueue.ToArray is a stable snapshot. Concurrent
        // Acquire/Return on the queue continues unimpeded; we operate read-only here, marking
        // unhealthy items via LastReturnedAt = MinValue for the shrink pass to evict.
        if (_options.Check is { } check)
        {
            var snapshot = _pool.Idle.ToArray();
            foreach (var entry in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                totalChecked++;
                using var hcSpan = _pool.Telemetry.StartHealthCheckSpan(_options.PoolName);
                bool isUnhealthy = false;
                Exception? thrown = null;
                try
                {
                    var state = await check(entry.Item, ct).ConfigureAwait(false);
                    if (state == PoolState.Unhealthy) isUnhealthy = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { isUnhealthy = true; thrown = ex; }

                if (isUnhealthy)
                {
                    unhealthy++;
                    _pool.Telemetry.OnHealthFailure();
                    _pool.Log.CheckUnhealthy(_options.PoolName, thrown?.GetType().Name ?? "Unhealthy");
                    try
                    {
                        await _pool.Options.FailurePolicy.HandleAsync(entry.Item, FailureKind.AfterUseUnhealthy, thrown, ct).ConfigureAwait(false);
                    }
                    catch { /* policy failure — keep sweeping */ }
                    // ConcurrentQueue<T> can't remove a specific element. Mark the entry stale via
                    // LastReturnedAt = MinValue so the shrink pass evicts it on this tick (or the
                    // next, whichever wins the cooldown gate first). DecrementTotal is NOT called
                    // here — the shrink pass owns the dequeue+decrement to keep _total balanced.
                    entry.LastReturnedAt = DateTimeOffset.MinValue;
                    hcSpan?.SetTag(PoolMeterNames.OutcomeTag, "unhealthy");
                }
                else
                {
                    hcSpan?.SetTag(PoolMeterNames.OutcomeTag, "healthy");
                }
                sweepSpan?.AddEvent(new ActivityEvent(
                    "item-checked",
                    tags: new ActivityTagsCollection
                    {
                        { "result", isUnhealthy ? "unhealthy" : "healthy" }
                    }));
            }
        }

        // ---- (2) Shrink pass ----
        // Cooldown gate: SinceLastGrowTicks must have reached ShrinkCooldownWindows before any
        // shrink is considered. Floor: never go below MinSize. Gentle decay: at most 1 item/tick.
        if (_pool.SinceLastGrowTicks >= _options.ShrinkCooldownWindows
            && _pool.CurrentTotal > _options.MinSize)
        {
            var now = _options.TimeProvider.GetUtcNow();
            if (_pool.Idle.TryPeek(out var head))
            {
                if (head.LastReturnedAt + _options.IdleTimeout <= now)
                {
                    if (_pool.Idle.TryDequeue(out var evict))
                    {
                        var oldTotal = _pool.CurrentTotal;
                        _pool.DecrementTotal();
                        var newTotal = _pool.CurrentTotal;
                        using var shrinkSpan = _pool.Telemetry.StartShrinkSpan(_options.PoolName, oldTotal, newTotal);
                        if (_options.Release is { } release)
                        {
                            try { await release(evict.Item, ct).ConfigureAwait(false); } catch { /* swallow */ }
                        }
                        _pool.Telemetry.OnShrink();
                        _pool.Log.Shrunk(_options.PoolName, oldTotal, newTotal);
                        shrinkSpan?.SetTag(PoolMeterNames.OutcomeTag, "shrunk");
                        shrunk = 1;
                    }
                }
            }
        }

        // ---- (3) Cooldown bookkeeping ----
        _pool.IncrementSinceLastGrowTicks();

        // ---- (4) Backoff state update ----
        var prevInterval = _backoff.CurrentInterval;
        _backoff.OnSweepResult(totalChecked, unhealthy);
        if (_backoff.IntervalChanged && _backoff.CurrentInterval > prevInterval)
        {
            _pool.Log.SweepFailureBackoff(
                _options.PoolName,
                prevInterval.TotalSeconds,
                _backoff.CurrentInterval.TotalSeconds,
                _backoff.ConsecutiveFailureWindows);
        }

        // ---- (5) Tick close ----
        var elapsed = _options.TimeProvider.GetElapsedTime(sweepStart);
        _pool.Telemetry.OnSweepDuration(elapsed);
        _pool.Log.SweepCompleted(_options.PoolName, elapsed.TotalMilliseconds, totalChecked, unhealthy, shrunk);
        sweepSpan?.SetTag(PoolMeterNames.OutcomeTag, unhealthy > 0 ? "unhealthy" : "healthy");
    }

    public async ValueTask DisposeAsync()
    {
        try { _sweepCts.Cancel(); } catch { /* swallow */ }
        try { await _sweepTask.ConfigureAwait(false); } catch { /* cancellation expected */ }
        _sweepCts.Dispose();
    }
}

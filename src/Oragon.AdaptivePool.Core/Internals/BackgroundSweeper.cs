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
    private DateTimeOffset? _lowPressureSince;

    // Test-only probe; resets each tick. NOT exposed via PublicAPI — internal-only signal for
    // Plan 03 tests via [InternalsVisibleTo].
    private TaskCompletionSource _tickCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // WR-03 fix: _tickCompleted is updated via Interlocked.Exchange on the sweep thread; readers
    // (test threads) MUST use Volatile.Read so they observe the freshly-installed TCS rather than
    // a stale (already-completed) one from a prior tick. Without this fence, ARM64 readers can
    // see the old TCS, await it, and observe synchronous completion — bypassing the wait.
    internal Task TickCompleted => Volatile.Read(ref _tickCompleted).Task;
    internal long TickCount; // diagnostic counter; Volatile.Read in tests

    public BackgroundSweeper(AdaptivePool<T> pool, Builder.AdaptivePoolOptions<T> options, SweepBackoffState backoff, CancellationToken lifetimeToken)
    {
        _pool = pool;
        _options = options;
        _backoff = backoff;
        _lowPressureSince = options.TimeProvider.GetUtcNow();
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
        // unhealthy items in PendingDiscard so the eviction pass or dequeue paths discard them.
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
                        // WR-05 fix: this is a Check-hook (background sweep) verdict, not an
                        // AfterUse hook verdict. A custom IItemFailurePolicy that branches on
                        // FailureKind needs to distinguish these to apply correct remediation.
                        await _pool.Options.FailurePolicy.HandleAsync(entry.Item, FailureKind.CheckUnhealthy, thrown, ct).ConfigureAwait(false);
                    }
                    catch { /* policy failure — keep sweeping */ }
                    // CR-03 fix: register the entry in PendingDiscard so EVERY dequeue path
                    // (Acquire, AcquireAsync, etc.) skips it immediately — independent of the
                    // shrink cooldown gate. Cooldown only governs SIZE-based shrink; broken
                    // items must NEVER be served to consumers regardless of cooldown state.
                    // The eviction pass below removes the entry from the queue eagerly.
                    _pool.PendingDiscard.TryAdd(entry, 0);
                    // Keep LastReturnedAt = MinValue as a backwards-compat signal (some unit
                    // tests still rely on observing a stale timestamp on unhealthy entries).
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

        // ---- (2a) Unhealthy eviction pass (CR-03) ----
        // Drain pending-discard entries from the queue head. Bypasses the shrink cooldown gate
        // and the MinSize floor — broken items must NEVER be served regardless of pool size.
        // Note: TryDequeueIdle (used by Acquire) already skips and discards pending-discard
        // entries lazily; this pass eagerly removes them from the head so they don't block
        // legitimate items that are queued behind them. Items not at the head will be drained
        // either by the next Acquire or by subsequent sweep ticks as the queue rotates.
        if (!_pool.PendingDiscard.IsEmpty)
        {
            // Bounded by current idle count to avoid spinning if a sibling thread races.
            int maxScan = _pool.Idle.Count;
            for (int i = 0; i < maxScan; i++)
            {
                if (!_pool.Idle.TryPeek(out var head)) break;
                if (!_pool.PendingDiscard.ContainsKey(head)) break; // healthy item at head — stop
                if (!_pool.Idle.TryDequeue(out var evict)) break;
                if (_pool.PendingDiscard.TryRemove(evict, out _))
                {
                    _pool.DecrementTotal();
                    if (_options.Release is { } release)
                    {
                        try { await release(evict.Item, ct).ConfigureAwait(false); } catch { /* swallow */ }
                    }
                }
                else
                {
                    // Concurrent acquirer already discarded this entry (raced through TryDequeueIdle).
                    // The dequeued entry is healthy or already-handled by another path — re-enqueue it.
                    _pool.Idle.Enqueue(evict);
                    break;
                }
            }
        }

        // ---- (2b) Shrink pass ----
        // Shrink is based on sustained aggregate low pressure, not per-item idle age.
        // Ring-buffer/FIFO reuse can refresh every item's LastReturnedAt even when the
        // pool clearly owns excess capacity. The aggregate signals below represent the
        // actual shape: available vs in-use vs waiters.
        var now = _options.TimeProvider.GetUtcNow();
        var currentTotal = _pool.CurrentTotal;
        var currentInUse = _pool.InUse;
        var currentWaiting = _pool.Waiting;
        var currentAvailable = _pool.Available;
        var utilization = currentTotal == 0 ? 0.0 : (double)currentInUse / currentTotal;
        var lowPressure =
            currentWaiting == 0
            && currentTotal > _options.MinSize
            && currentAvailable > 0
            && utilization <= _options.ShrinkOnUtilizationPercent;

        if (!lowPressure)
        {
            _lowPressureSince = null;
        }
        else
        {
            _lowPressureSince ??= now;
            if (_pool.SinceLastGrowTicks >= _options.ShrinkCooldownWindows
                && _lowPressureSince.Value + _options.IdleTimeout <= now)
            {
                var targetTotal = currentInUse == 0
                    ? _options.MinSize
                    : Math.Max(_options.MinSize, (int)Math.Ceiling(currentInUse / _options.ShrinkTargetUtilizationPercent));
                var removable = Math.Min(_options.ShrinkBatchSize, Math.Min(currentAvailable, currentTotal - targetTotal));
                for (var i = 0; i < removable; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var oldTotal = _pool.CurrentTotal;
                    if (oldTotal <= _options.MinSize || oldTotal <= targetTotal) break;
                    if (!_pool.Idle.TryDequeue(out var evict)) break;

                    _pool.PendingDiscard.TryRemove(evict, out _);
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
                    shrunk++;
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

namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// PeriodicTimer-driven background sweeper. The loop body (<see cref="RunSweepTickAsync"/>) is a
/// stub for Plan 01 — it only advances the cooldown counter and records a clean sweep on the
/// backoff state. Plan 02 replaces the body with health-check + shrink + telemetry.
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
                    await RunSweepTickAsync(_sweepCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    prevTcs.TrySetResult();
                    break;
                }
                catch (Exception)
                {
                    // Plan 02 will route to PoolDiagnosticsLog.SweepFailed. For Plan 01,
                    // swallow defensively so a single tick failure cannot kill the loop.
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

    // Plan 01 stub: increments the since-last-grow counter only. Plan 02 replaces this body
    // with health-check pass + shrink pass + telemetry. The "clean sweep" call below keeps
    // the backoff state at base interval for Phase 2's Plan 03 tests that observe interval
    // stability under no-op sweeps.
    private ValueTask RunSweepTickAsync(CancellationToken ct)
    {
        _pool.IncrementSinceLastGrowTicks();
        _backoff.OnSweepResult(totalChecked: 0, unhealthy: 0);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { _sweepCts.Cancel(); } catch { /* swallow */ }
        try { await _sweepTask.ConfigureAwait(false); } catch { /* cancellation expected */ }
        _sweepCts.Dispose();
    }
}

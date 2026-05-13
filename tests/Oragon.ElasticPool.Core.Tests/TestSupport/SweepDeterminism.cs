using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Core.Internals;

namespace Oragon.ElasticPool.Core.Tests.TestSupport;

/// <summary>
/// Helpers for the FakeTimeProvider+PeriodicTimer race documented in RESEARCH §"Pattern 9":
/// <c>Task.Run(SweepLoopAsync)</c> may not have entered <c>WaitForNextTickAsync</c> by the
/// time a test calls <c>fake.Advance(...)</c>; without this yield the timer fires before the
/// loop subscribes and the tick is missed. The yield + small delay primes the loop.
/// </summary>
internal static class SweepDeterminism
{
    /// <summary>
    /// Yields the calling task and briefly delays to let the sweep loop reach
    /// <c>WaitForNextTickAsync</c> before the test calls <c>fake.Advance(...)</c>.
    /// </summary>
    public static async Task PrimeAsync<T>(ElasticPool<T> pool) where T : notnull
    {
        await Task.Yield();
        // Brief wall-clock delay (100 ms) to let Task.Run(SweepLoopAsync) execute up to
        // its first PeriodicTimer.WaitForNextTickAsync — the only wall-clock cost in the
        // suite. 50 ms is empirically sufficient on net8/9/10 even under -j 2 parallel test
        // execution; we use 100 ms for safety margin.
        await Task.Delay(100);
    }

    /// <summary>
    /// Drives one sweep tick via FakeTimeProvider deterministically:
    /// (1) capture the pre-Advance TickCompleted TCS, (2) advance, (3) await the captured TCS.
    /// Using <see cref="Task.WhenAny(Task, Task)"/> with a 2s wall-clock fallback so a missed
    /// tick fails fast rather than hanging the test.
    /// </summary>
    public static async Task AdvanceAndAwaitTickAsync<T>(
        ElasticPool<T> pool,
        FakeTimeProvider fake,
        TimeSpan amount) where T : notnull
    {
        var tcs = pool.Sweeper.TickCompleted;
        fake.Advance(amount);
        var winner = await Task.WhenAny(tcs, Task.Delay(TimeSpan.FromSeconds(2)));
        if (winner != tcs)
            throw new TimeoutException(
                $"Sweep tick did not complete within 2s after fake.Advance({amount}). " +
                "Likely the sweep loop had not subscribed yet — call PrimeAsync(pool) first.");
    }
}

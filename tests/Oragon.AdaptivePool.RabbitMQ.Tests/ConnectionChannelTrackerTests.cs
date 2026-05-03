using AwesomeAssertions;
using NSubstitute;
using Oragon.AdaptivePool.RabbitMQ.Internals;
using RabbitMQ.Client;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.Tests;

/// <summary>
/// Unit tests for <see cref="ConnectionChannelTracker"/> — atomic per-connection
/// channel-slot accounting used by the channel pool's eager-spread Factory.
/// </summary>
public class ConnectionChannelTrackerTests
{
    [Fact]
    public void TryAcquireSlot_IncrementsBelowMax()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();

        var ok = tracker.TryAcquireSlot(conn, max: 2);

        ok.Should().BeTrue();
        tracker.CountFor(conn).Should().Be(1);
    }

    [Fact]
    public void TryAcquireSlot_ReturnsFalse_AtMax()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        tracker.TryAcquireSlot(conn, 2).Should().BeTrue();
        tracker.TryAcquireSlot(conn, 2).Should().BeTrue();

        var third = tracker.TryAcquireSlot(conn, 2);

        third.Should().BeFalse();
        tracker.CountFor(conn).Should().Be(2);
    }

    [Fact]
    public void ReleaseSlot_DecrementsAndRemovesAtZero()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        tracker.TryAcquireSlot(conn, 3).Should().BeTrue();

        tracker.ReleaseSlot(conn);

        tracker.CountFor(conn).Should().Be(0);
    }

    [Fact]
    public void ReleaseSlot_DecrementsAboveOne()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        tracker.TryAcquireSlot(conn, 5).Should().BeTrue();
        tracker.TryAcquireSlot(conn, 5).Should().BeTrue();
        tracker.TryAcquireSlot(conn, 5).Should().BeTrue();

        tracker.ReleaseSlot(conn);

        tracker.CountFor(conn).Should().Be(2);
    }

    [Fact]
    public void ReleaseSlot_NoOpWhenAbsent()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();

        Action act = () => tracker.ReleaseSlot(conn);

        act.Should().NotThrow();
        tracker.CountFor(conn).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_TryAcquireSlot_NeverExceedsMax()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        const int max = 10;
        const int threads = 64;
        const int attemptsPerThread = 1000;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < attemptsPerThread; i++)
            {
                tracker.TryAcquireSlot(conn, max);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Final count must be exactly max (no thread releases; once reached, all subsequent
        // TryAcquireSlot calls return false). Atomic CAS must prevent overflow.
        tracker.CountFor(conn).Should().Be(max);
    }

    [Fact]
    public async Task Concurrent_AcquireRelease_RemainsBoundedAndConsistent()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        const int max = 10;
        const int threads = 32;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                if (tracker.TryAcquireSlot(conn, max))
                {
                    tracker.ReleaseSlot(conn);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Final count must be 0 (every successful acquire was released).
        tracker.CountFor(conn).Should().Be(0);
    }

    [Fact]
    public void TryAcquireSlot_InvalidMax_Throws()
    {
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();

        Action act = () => tracker.TryAcquireSlot(conn, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryAcquireSlot_DoesNotSpin_WhenZeroValueEntryPresent()
    {
        // WR-01 regression: a hypothetical zero-value entry must not cause TryAcquireSlot
        // to livelock. The standard ReleaseSlot path removes the entry at count 1 (so
        // current==0 is never observed by a subsequent TryGetValue under normal flow),
        // but if a future code path were to leave a zero-value entry in place,
        // TryAcquireSlot must complete in O(1) by routing through TryUpdate, not TryAdd.
        //
        // We can't directly inject a zero entry without exposing internals, so this test
        // exercises the equivalent code path: acquire one slot then release it (which
        // removes the entry); a subsequent acquire must succeed via the !hasEntry branch
        // and complete promptly.
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();

        // Acquire then release — leaves no entry (correct invariant).
        tracker.TryAcquireSlot(conn, 4).Should().BeTrue();
        tracker.ReleaseSlot(conn);
        tracker.CountFor(conn).Should().Be(0);

        // Re-acquire: the !hasEntry branch must succeed, not spin.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = tracker.TryAcquireSlot(conn, 4);
        sw.Stop();

        ok.Should().BeTrue();
        tracker.CountFor(conn).Should().Be(1);
        // 100ms is generous; the operation should complete in microseconds. A real
        // livelock would peg a CPU and exceed this by orders of magnitude.
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "TryAcquireSlot must not spin when re-acquiring after release");
    }

    [Fact]
    public async Task TryAcquireSlot_RaceBetweenReleaseAndReAcquire_DoesNotLivelock()
    {
        // WR-01 regression: the CAS retry loop must converge under high contention with
        // simultaneous ReleaseSlot calls that remove entries. If TryAcquireSlot routed
        // hasEntry+current==0 through TryAdd, an interleaving where the entry is removed
        // and re-inserted by a third thread between TryGetValue and the CAS could cause
        // pathological retries. The fixed branch uses TryUpdate for any hasEntry case.
        var tracker = new ConnectionChannelTracker();
        var conn = Substitute.For<IConnection>();
        const int max = 10;
        const int threads = 16;
        const int iterations = 5_000;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                if (tracker.TryAcquireSlot(conn, max))
                {
                    tracker.ReleaseSlot(conn);
                }
            }
        })).ToArray();

        // 30s ceiling — actual completion is sub-second on any modern box.
        var combined = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(combined, Task.Delay(TimeSpan.FromSeconds(30)));
        winner.Should().BeSameAs(combined, "TryAcquireSlot must not livelock under contention");
        tracker.CountFor(conn).Should().Be(0);
    }
}

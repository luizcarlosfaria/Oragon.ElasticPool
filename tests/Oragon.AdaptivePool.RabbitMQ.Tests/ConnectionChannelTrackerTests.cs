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
}

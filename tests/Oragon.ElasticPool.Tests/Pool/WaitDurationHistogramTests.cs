using AwesomeAssertions;
using Oragon.ElasticPool.Internals;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

public class WaitDurationHistogramTests
{
    [Fact]
    public void P95_OnEmpty_ReturnsZero()
    {
        var hist = new WaitDurationHistogram();

        hist.P95.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void P95_With10Samples_ReturnsCorrectPercentile()
    {
        var hist = new WaitDurationHistogram();
        for (int ms = 1; ms <= 10; ms++)
        {
            hist.Record(TimeSpan.FromMilliseconds(ms));
        }

        // ceiling(10 * 0.95) - 1 = 9 → snapshot[9] = 10ms (sorted, 0-based)
        hist.P95.Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void P95_With100Samples_HandlesExactPercentile()
    {
        var hist = new WaitDurationHistogram();
        for (int ms = 1; ms <= 100; ms++)
        {
            hist.Record(TimeSpan.FromMilliseconds(ms));
        }

        // ceiling(100 * 0.95) - 1 = 94 → snapshot[94] = 95ms
        hist.P95.Should().Be(TimeSpan.FromMilliseconds(95));
    }

    [Fact]
    public void P95_OverwritesAfterCapacity()
    {
        var hist = new WaitDurationHistogram();
        for (int ms = 1; ms <= 200; ms++)
        {
            hist.Record(TimeSpan.FromMilliseconds(ms));
        }

        // Last 100 retained: 101..200ms; ceiling(100*0.95)-1 = 94 → snapshot[94] = 195ms
        hist.P95.Should().Be(TimeSpan.FromMilliseconds(195));
    }

    [Fact]
    public async Task Record_DoesNotThrow_UnderConcurrency()
    {
        var hist = new WaitDurationHistogram();
        const int threads = 50;
        const int perThread = 1000;

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                hist.Record(TimeSpan.FromMilliseconds((t * perThread + i) % 500));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var p95 = hist.P95;
        p95.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        p95.Should().BeLessThan(TimeSpan.FromSeconds(1)); // sane bound
    }
}

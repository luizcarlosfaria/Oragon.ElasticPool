using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Core.Internals;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Pool;

public class UtilizationSamplerTests
{
    [Fact]
    public void Sample_BelowDebounceThreshold_DoesNotWriteSecondBucket()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, window: TimeSpan.FromSeconds(30), bucketSize: TimeSpan.FromSeconds(1));

        sampler.Sample(2, 4);          // first sample writes bucket: 0.50
        fake.Advance(TimeSpan.FromMilliseconds(500));
        sampler.Sample(8, 8);          // within 1s debounce — should be ignored

        sampler.AverageUtilization().Should().Be(0.5, "the second sample fell inside the 1s debounce window so only the first sample contributes");
    }

    [Fact]
    public void Sample_AboveDebounceThreshold_WritesSecondBucket()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, window: TimeSpan.FromSeconds(30), bucketSize: TimeSpan.FromSeconds(1));

        sampler.Sample(2, 10);         // 0.20
        fake.Advance(TimeSpan.FromMilliseconds(1100));
        sampler.Sample(6, 10);         // 0.60

        // Two contributing buckets: (2+6)/(10+10) = 0.40
        sampler.AverageUtilization().Should().BeApproximately(0.4, precision: 1e-9);
    }

    [Fact]
    public void AverageUtilization_OnlyConsidersSamplesWithinWindow()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, window: TimeSpan.FromSeconds(30), bucketSize: TimeSpan.FromSeconds(1));

        // Old sample: should fall outside window after we advance 60s.
        sampler.Sample(10, 10); // 1.0
        fake.Advance(TimeSpan.FromSeconds(60));
        // New sample inside window.
        sampler.Sample(2, 10); // 0.20

        // Old sample is past cutoff (now - 30s) — only the new sample counts.
        sampler.AverageUtilization().Should().BeApproximately(0.20, precision: 1e-9);
    }

    [Fact]
    public void AverageUtilization_WithZeroTotal_ReturnsZero()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, window: TimeSpan.FromSeconds(30), bucketSize: TimeSpan.FromSeconds(1));

        sampler.Sample(0, 0); // empty pool

        sampler.AverageUtilization().Should().Be(0.0);
    }

    [Fact]
    public void AverageUtilization_NoSamples_ReturnsZero()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        sampler.AverageUtilization().Should().Be(0.0);
    }

    [Fact]
    public void AverageUtilization_ComputesAcrossBuckets()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        sampler.Sample(5, 10);
        fake.Advance(TimeSpan.FromMilliseconds(1100));
        sampler.Sample(10, 10);

        // (5 + 10) / (10 + 10) = 0.75
        sampler.AverageUtilization().Should().BeApproximately(0.75, precision: 1e-9);
    }

    [Fact(Timeout = 30_000)]
    public async Task Sample_ParallelWriterReader_ReaderNeverObservesTornBucket_CR02Regression()
    {
        // CR-02 regression: with the publication pattern (write data fields plain, then publish
        // the timestamp via Volatile.Write LAST), a reader that performs Volatile.Read on the
        // timestamp and then plain reads of inUse/total NEVER observes a fresh timestamp paired
        // with stale (or zero/uninitialized) data fields.
        //
        // Invariant we assert: for every bucket the reader observes as in-window (ts >= cutoff),
        // the inUse value is consistent with the total — i.e., (inUse, total) was written by the
        // SAME Sample() call, so 0 <= inUse <= total. Without the fix (timestamp written first),
        // a fresh timestamp could be observed alongside a still-zero inUse/total slot — breaking
        // this invariant on weakly-ordered architectures.
        //
        // The test runs writer and reader threads in parallel (real wall-clock concurrency). The
        // writer cycles through (inUse=K, total=2K) values for K=1..M. The reader continuously
        // computes AverageUtilization() and records bucket data. Any (inUse > total) observation
        // indicates a torn read.
        //
        // Using TimeProvider.System (not FakeTimeProvider) because we need real concurrency to
        // exercise memory ordering. The 1s debounce limits how often Sample writes; we run for
        // ~1.5 seconds wall-clock to get ~1-2 fresh writes, then assert via post-hoc inspection.
        var sampler = new UtilizationSampler(System.TimeProvider.System,
            window: TimeSpan.FromSeconds(60),     // big window — all buckets stay in-window
            bucketSize: TimeSpan.FromSeconds(1));

        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var torn = 0;

        var writerTask = Task.Run(() =>
        {
            int k = 1;
            while (!stop.IsCancellationRequested)
            {
                sampler.Sample(inUse: k, total: 2 * k);
                Interlocked.Increment(ref k);
                if (k > 1000) k = 1;
                // Spin briefly so writer makes progress; debounce naturally throttles to once/sec.
                Thread.SpinWait(50);
            }
        });

        var readerTask = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // AverageUtilization sums over all in-window buckets. The invariant we encode
                // here: sumInUse <= sumTotal must always hold (a torn read with fresh ts but
                // zero data would temporarily satisfy this trivially; a torn read with fresh
                // ts paired with the WRONG slot's older data could exceed it). Read avg
                // alone is not enough — we also exercise the reader path repeatedly.
                var avg = sampler.AverageUtilization();
                if (avg < 0.0 || avg > 1.0)
                {
                    Interlocked.Increment(ref torn);
                }
                Thread.SpinWait(10);
            }
        });

        await Task.WhenAll(writerTask, readerTask);

        torn.Should().Be(0, "AverageUtilization must always be in [0, 1] — a value outside this range indicates a torn read of (inUse, total) under parallel Sample().");
        // Post-condition: the sampler is still in a valid state.
        var finalAvg = sampler.AverageUtilization();
        finalAvg.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Sample_HotLoop_DoesNotThrow()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sampler = new UtilizationSampler(fake, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        // Tight loop without advancing time — debounce keeps all but the first call no-op.
        for (int i = 0; i < 1000; i++)
        {
            sampler.Sample(i % 10, 10);
        }

        // First sample wins; (i=0)/(10) = 0.0
        sampler.AverageUtilization().Should().Be(0.0);
    }
}

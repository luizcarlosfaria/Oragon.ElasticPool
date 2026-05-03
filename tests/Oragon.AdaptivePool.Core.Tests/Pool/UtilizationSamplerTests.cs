using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Oragon.AdaptivePool.Core.Internals;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

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

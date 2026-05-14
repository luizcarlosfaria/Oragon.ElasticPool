using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Hooks;
using Oragon.ElasticPool.Internals;
using Oragon.ElasticPool.Policies;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Pool;

public class PressureSamplerTests
{
    private static ElasticPoolOptions<Resource> BuildOptions(
        FakeTimeProvider fake,
        int maxSize = 10,
        int growOnWaiterCount = 1,
        double growOnUtilizationPercent = 0.80,
        TimeSpan? growOnWaitTimeP95 = null)
    {
        return new ElasticPoolOptions<Resource>
        {
            Factory = (s, ct) => ValueTask.FromResult(new Resource()),
            MinSize = 0,
            MaxSize = maxSize,
            InitialSize = 0,
            FailurePolicy = new DiscardAndReplaceFailurePolicy<Resource>(),
            TimeProvider = fake,
            GrowOnWaiterCount = growOnWaiterCount,
            GrowOnUtilizationPercent = growOnUtilizationPercent,
            GrowOnWaitTimeP95 = growOnWaitTimeP95 ?? TimeSpan.FromMilliseconds(100),
        };
    }

    private static (PressureSampler<Resource> sampler, UtilizationSampler util, WaitDurationHistogram wait, FakeTimeProvider fake) Build(
        ElasticPoolOptions<Resource> options,
        FakeTimeProvider fake)
    {
        var util = new UtilizationSampler(fake, options.UtilizationWindow, TimeSpan.FromSeconds(1));
        var wait = new WaitDurationHistogram();
        return (new PressureSampler<Resource>(options, util, wait), util, wait, fake);
    }

    [Fact]
    public void Evaluate_AtMaxSize_ReturnsShouldGrowFalse_AndAllTripsFalse()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sampler, _, _, _) = Build(BuildOptions(fake, maxSize: 5), fake);

        var result = sampler.Evaluate(currentTotal: 5, currentWaiters: 100);

        result.ShouldGrow.Should().BeFalse();
        result.TrippedByWaiters.Should().BeFalse();
        result.TrippedByUtilization.Should().BeFalse();
        result.TrippedByP95.Should().BeFalse();
        result.CurrentTotal.Should().Be(5);
    }

    [Fact]
    public void Evaluate_BelowAllThresholds_ReturnsShouldGrowFalse()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sampler, _, _, _) = Build(BuildOptions(fake, growOnWaiterCount: 5), fake);

        var result = sampler.Evaluate(currentTotal: 2, currentWaiters: 0);

        result.ShouldGrow.Should().BeFalse();
        result.TrippedByWaiters.Should().BeFalse();
        result.TrippedByUtilization.Should().BeFalse();
        result.TrippedByP95.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OnlyWaiterCountTrips_ReturnsTrue_WithTrippedByWaitersTrue()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = BuildOptions(
            fake,
            growOnWaiterCount: 1,
            growOnUtilizationPercent: 1.01,                            // unreachable
            growOnWaitTimeP95: TimeSpan.FromHours(1));                  // unreachable
        var (sampler, _, _, _) = Build(options, fake);

        var result = sampler.Evaluate(currentTotal: 2, currentWaiters: 1);

        result.ShouldGrow.Should().BeTrue();
        result.TrippedByWaiters.Should().BeTrue();
        result.TrippedByUtilization.Should().BeFalse();
        result.TrippedByP95.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OnlyUtilizationTrips_ReturnsTrue_WithTrippedByUtilizationTrue()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = BuildOptions(
            fake,
            growOnWaiterCount: int.MaxValue,                            // unreachable
            growOnUtilizationPercent: 0.50,
            growOnWaitTimeP95: TimeSpan.FromHours(1));                  // unreachable
        var (sampler, util, _, _) = Build(options, fake);

        // Push utilization above 50%. Sample once at (8/10) = 0.80, accounting for 1s debounce.
        util.Sample(8, 10);
        fake.Advance(TimeSpan.FromMilliseconds(1100));
        util.Sample(8, 10);

        var result = sampler.Evaluate(currentTotal: 5, currentWaiters: 0);

        result.ShouldGrow.Should().BeTrue();
        result.TrippedByUtilization.Should().BeTrue();
        result.TrippedByWaiters.Should().BeFalse();
        result.TrippedByP95.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OnlyP95Trips_ReturnsTrue_WithTrippedByP95True()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = BuildOptions(
            fake,
            growOnWaiterCount: int.MaxValue,                            // unreachable
            growOnUtilizationPercent: 1.01,                             // unreachable
            growOnWaitTimeP95: TimeSpan.FromMilliseconds(100));
        var (sampler, _, wait, _) = Build(options, fake);

        for (int i = 0; i < 100; i++)
        {
            wait.Record(TimeSpan.FromMilliseconds(150));
        }

        var result = sampler.Evaluate(currentTotal: 5, currentWaiters: 0);

        result.ShouldGrow.Should().BeTrue();
        result.TrippedByP95.Should().BeTrue();
        result.TrippedByWaiters.Should().BeFalse();
        result.TrippedByUtilization.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_AllThreeTrip_ReturnsTrue_WithAllFlagsTrue()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = BuildOptions(
            fake,
            growOnWaiterCount: 1,
            growOnUtilizationPercent: 0.50,
            growOnWaitTimeP95: TimeSpan.FromMilliseconds(100));
        var (sampler, util, wait, _) = Build(options, fake);

        util.Sample(8, 10);
        fake.Advance(TimeSpan.FromMilliseconds(1100));
        util.Sample(8, 10);
        for (int i = 0; i < 100; i++) wait.Record(TimeSpan.FromMilliseconds(200));

        var result = sampler.Evaluate(currentTotal: 5, currentWaiters: 1);

        result.ShouldGrow.Should().BeTrue();
        result.TrippedByWaiters.Should().BeTrue("OR semantics: no short-circuit, all signals are evaluated for telemetry");
        result.TrippedByUtilization.Should().BeTrue();
        result.TrippedByP95.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RespectsCustomThresholds()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = BuildOptions(
            fake,
            growOnWaiterCount: int.MaxValue,
            growOnUtilizationPercent: 0.50,
            growOnWaitTimeP95: TimeSpan.FromHours(1));
        var (sampler, util, _, _) = Build(options, fake);

        util.Sample(5, 10);  // 0.50
        fake.Advance(TimeSpan.FromMilliseconds(1100));
        util.Sample(5, 10);

        var result = sampler.Evaluate(currentTotal: 5, currentWaiters: 0);

        result.ShouldGrow.Should().BeTrue();
        result.TrippedByUtilization.Should().BeTrue();
    }
}

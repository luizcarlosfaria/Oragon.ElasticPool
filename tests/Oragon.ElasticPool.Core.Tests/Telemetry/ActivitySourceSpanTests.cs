using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;
using Oragon.ElasticPool.Core.DependencyInjection;
using Oragon.ElasticPool.Core.Internals;
using Oragon.ElasticPool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests.Telemetry;

/// <summary>
/// Each test uses a unique pool name (Guid-suffixed) and filters captured spans by
/// <c>pool.name</c> tag. xUnit v3 runs tests in parallel and ActivitySource is a
/// process-static singleton — without filtering, spans from sibling tests bleed in.
/// </summary>
public class ActivitySourceSpanTests
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact(Timeout = 15_000)]
    public async Task AcquireAsync_EmitsPoolAcquireSpan_WithOkOutcome()
    {
        var poolName = Unique("acquire");
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await pool.ReadyAsync();

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await using (await pool.AcquireAsync()) { }

            var acquireSpans = captured.ByNameAndPool("Pool.Acquire", poolName);
            acquireSpans.Should().NotBeEmpty();
            acquireSpans[0].GetTagItem("outcome").Should().Be("ok");
            acquireSpans[0].GetTagItem("pool.name").Should().Be(poolName);
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task AcquireAsync_Cancellation_EmitsAcquireSpan_WithCanceledOutcome()
    {
        var poolName = Unique("acquire-cancel");
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await pool.ReadyAsync();

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            // Hold the only item; second acquire parks; cancel.
            var holder = await pool.AcquireAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Func<Task> attempt = async () => await pool.AcquireAsync(cts.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            var canceledSpans = captured.ByNameAndPool("Pool.Acquire", poolName)
                .Where(a => (string?)a.GetTagItem("outcome") == "canceled").ToArray();
            canceledSpans.Should().NotBeEmpty();

            await holder.DisposeAsync();
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task Grow_EmitsPoolGrowSpan_WithTripFlags()
    {
        var poolName = Unique("grow");
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 5, 0)
            .GrowOnWaiterCount(1));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await using (await pool.AcquireAsync()) { }

            var growSpans = captured.ByNameAndPool("Pool.Grow", poolName);
            growSpans.Should().ContainSingle();
            growSpans[0].GetTagItem("grow.tripped_by_waiters").Should().Be(true);
            growSpans[0].GetTagItem("pool.name").Should().Be(poolName);
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Shrink_EmitsPoolShrinkSpan_WithSizeBeforeAfter()
    {
        var poolName = Unique("shrink");
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(1, 10, 3)
            .WithTimeProvider(fake)
            .IdleTimeout(TimeSpan.FromSeconds(10))
            .ShrinkCooldownWindows(0)
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = (ElasticPool<Resource>)sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            fake.Advance(TimeSpan.FromSeconds(15));
            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            var shrinkSpans = captured.ByNameAndPool("Pool.Shrink", poolName);
            shrinkSpans.Should().ContainSingle();
            shrinkSpans[0].GetTagItem("pool.size_before").Should().Be(3);
            shrinkSpans[0].GetTagItem("pool.size_after").Should().Be(2);
            shrinkSpans[0].GetTagItem("pool.name").Should().Be(poolName);
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Sweep_EmitsOneSpanPerTick_WithPerItemEvents()
    {
        var poolName = Unique("sweep");
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(5, 10, 5)
            .WithTimeProvider(fake)
            .Check((r, ct) => ValueTask.FromResult(PoolState.Healthy))
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = (ElasticPool<Resource>)sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            var sweepSpans = captured.ByNameAndPool("Pool.Sweep", poolName);
            sweepSpans.Should().ContainSingle("exactly one Pool.Sweep span per tick (CONTEXT decision)");
            var sweepSpan = sweepSpans[0];
            sweepSpan.Events.Count().Should().Be(5, "one ActivityEvent per item-checked");
            sweepSpan.Events.All(e => e.Name == "item-checked").Should().BeTrue();
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task HealthCheck_EmitsPoolHealthCheckSpan_PerInvocation_WithHealthyOrUnhealthyOutcome()
    {
        var poolName = Unique("hc");
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        int call = 0;
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(3, 10, 3)
            .WithTimeProvider(fake)
            .Check((r, ct) =>
            {
                var c = Interlocked.Increment(ref call);
                var state = c == 2 ? PoolState.Unhealthy : PoolState.Healthy;
                return ValueTask.FromResult(state);
            })
            .SweepInterval(TimeSpan.FromSeconds(30)));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = (ElasticPool<Resource>)sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await pool.ReadyAsync();
            await SweepDeterminism.PrimeAsync(pool);

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await SweepDeterminism.AdvanceAndAwaitTickAsync(pool, fake, TimeSpan.FromSeconds(30));

            var hcSpans = captured.ByNameAndPool("Pool.HealthCheck", poolName);
            hcSpans.Count.Should().Be(3, "one HealthCheck span per Check invocation");
            hcSpans.Count(s => (string?)s.GetTagItem("outcome") == "healthy").Should().Be(2);
            hcSpans.Count(s => (string?)s.GetTagItem("outcome") == "unhealthy").Should().Be(1);
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task Spans_AllCarryPoolNameTag()
    {
        var poolName = Unique("named");
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 5, 0)
            .GrowOnWaiterCount(1));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);

            using var captured = new CapturedActivities("Oragon.ElasticPool");

            await using (await pool.AcquireAsync()) { }

            // Filter to just this pool's spans (parallel test bleed).
            var ours = captured.Stopped.Where(a =>
                (string?)a.GetTagItem("pool.name") == poolName).ToArray();
            ours.Should().NotBeEmpty();
            foreach (var span in ours)
            {
                span.GetTagItem("pool.name").Should().Be(poolName,
                    $"span '{span.OperationName}' must carry pool.name tag");
            }
        }
        finally { sp.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task NoListenersAttached_StartActivityReturnsNull_NoCrash()
    {
        var poolName = Unique("nolisten");
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddElasticPool<Resource>(poolName, b => b
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 5, 0)
            .GrowOnWaiterCount(1));
        var sp = services.BuildServiceProvider();
        try
        {
            var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>(poolName);
            await using (await pool.AcquireAsync()) { }
            // Survives without listeners — no exception.
        }
        finally { sp.Dispose(); }
    }
}

using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Exceptions;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class WaitBehaviorTests
{
    private static IAdaptivePool<Resource> Build(
        WaitBehavior behavior,
        Action<AdaptivePoolBuilder<Resource>>? configure = null)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var builder = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 1)
            .WhenExhausted(behavior);
        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task WaitBehaviorThrow_ExhaustedPool_ThrowsPoolExhaustedImmediately()
    {
        await using var pool = Build(WaitBehavior.Throw);
        await pool.ReadyAsync();
        await using var first = await pool.AcquireAsync();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PoolExhaustedException>(async () => await pool.AcquireAsync());
        sw.Stop();

        ex.MaxSize.Should().Be(1);
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "Throw mode must be immediate");
    }

    [Fact]
    public async Task WaitBehaviorWait_RespectsCancellationToken()
    {
        await using var pool = Build(WaitBehavior.Wait);
        await pool.ReadyAsync();
        await using var first = await pool.AcquireAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pool.AcquireAsync(cts.Token));

        ex.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task WaitBehaviorWait_TokenCanceledBeforeAcquire_ReturnsImmediately()
    {
        await using var pool = Build(WaitBehavior.Wait);
        await pool.ReadyAsync();
        await using var first = await pool.AcquireAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        // Channel.WriteAsync surfaces TaskCanceledException (an OperationCanceledException subclass)
        // when the token is already canceled — accept either by using Assert.ThrowsAnyAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pool.AcquireAsync(cts.Token));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public async Task WaitBehaviorWait_MaxWaiterCountZero_ThrowsInsteadOfParking()
    {
        await using var pool = Build(WaitBehavior.Wait, b => b.MaxWaiterCount(0));
        await pool.ReadyAsync();
        await using var first = await pool.AcquireAsync();

        var ex = await Assert.ThrowsAsync<PoolExhaustedException>(async () => await pool.AcquireAsync());

        ex.MaxSize.Should().Be(1);
        pool.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task WaitBehaviorWait_MaxWaiterCountRejectsExcessWaiters()
    {
        await using var pool = Build(WaitBehavior.Wait, b => b.MaxWaiterCount(1));
        await pool.ReadyAsync();
        await using var first = await pool.AcquireAsync();

        var secondTask = pool.AcquireAsync().AsTask();
        for (var i = 0; i < 50 && pool.Waiting == 0; i++)
            await Task.Delay(10);

        pool.Waiting.Should().Be(1);

        var ex = await Assert.ThrowsAsync<PoolExhaustedException>(async () => await pool.AcquireAsync());
        ex.MaxSize.Should().Be(1);
        pool.Waiting.Should().Be(1);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

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
    private static IAdaptivePool<Resource> Build(WaitBehavior behavior)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 1)
            .WhenExhausted(behavior)
            .Build();
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
}

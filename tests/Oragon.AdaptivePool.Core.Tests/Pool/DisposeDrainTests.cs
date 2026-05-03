using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class DisposeDrainTests
{
    [Fact]
    public async Task DisposeAsync_InvokesReleaseOnEachIdleEntry()
    {
        var releaseCount = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) => { Interlocked.Increment(ref releaseCount); return ValueTask.CompletedTask; })
            .WithBounds(minSize: 0, maxSize: 3, initialSize: 3)
            .Build();

        await pool.ReadyAsync();
        await pool.DisposeAsync();

        releaseCount.Should().Be(3, "Release must run on every idle entry during dispose");
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_NewAcquireThrowsObjectDisposed()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        await pool.DisposeAsync();

        Action sync = () => pool.Acquire();
        sync.Should().Throw<ObjectDisposedException>();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pool.AcquireAsync());
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var releaseCount = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) => { Interlocked.Increment(ref releaseCount); return ValueTask.CompletedTask; })
            .WithBounds(0, 2, 2)
            .Build();

        await pool.ReadyAsync();
        await pool.DisposeAsync();
        await pool.DisposeAsync(); // second call is no-op

        releaseCount.Should().Be(2, "second dispose must not re-invoke Release");
    }

    [Fact]
    public async Task Dispose_Sync_DrainsPoolWithoutHanging()
    {
        var releaseCount = 0;
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .Release((r, ct) => { Interlocked.Increment(ref releaseCount); return ValueTask.CompletedTask; })
            .WithBounds(0, 2, 2)
            .Build();

        await pool.ReadyAsync();

        var disposeTask = Task.Run(() => pool.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(disposeTask, "sync Dispose must drain via DisposeAsync without hanging");
        await disposeTask; // surface any inner exception
        releaseCount.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingWaiters()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .Build();

        await pool.ReadyAsync();
        var first = await pool.AcquireAsync();

        // Park a waiter (channel) — the only slot is taken.
        var waiterTask = Task.Run(async () => await pool.AcquireAsync());
        await Task.Delay(50);
        waiterTask.IsCompleted.Should().BeFalse();

        // Disposing the pool must cancel the parked waiter.
        var disposeTask = pool.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<Exception>(async () => await waiterTask.WaitAsync(TimeSpan.FromSeconds(3)));
        await disposeTask;

        // first item is now wrapped by a stale handle — disposing it must not throw.
        await first.DisposeAsync();
    }
}

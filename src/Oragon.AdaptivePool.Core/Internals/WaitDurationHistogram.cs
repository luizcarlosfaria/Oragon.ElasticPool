namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// 100-sample ring-buffered acquire-wait histogram. <see cref="Record"/> is lock-free (hot slow path);
/// <see cref="P95"/> takes a short lock to snapshot+sort the populated portion (O(n log n) on 100 entries — microseconds).
/// </summary>
internal sealed class WaitDurationHistogram
{
    private const int Capacity = 100;
    private readonly long[] _ticks = new long[Capacity];   // -1 = empty
    private int _writeIndex;
    private readonly object _readLock = new();

    public WaitDurationHistogram() { Array.Fill(_ticks, -1L); }

    public void Record(TimeSpan duration)
    {
        var idx = (int)((uint)Interlocked.Increment(ref _writeIndex) - 1) % Capacity;
        Volatile.Write(ref _ticks[idx], duration.Ticks);
    }

    public TimeSpan P95
    {
        get
        {
            lock (_readLock)
            {
                Span<long> snapshot = stackalloc long[Capacity];
                int populated = 0;
                for (int i = 0; i < Capacity; i++)
                {
                    var v = Volatile.Read(ref _ticks[i]);
                    if (v >= 0) snapshot[populated++] = v;
                }
                if (populated == 0) return TimeSpan.Zero;
                snapshot[..populated].Sort();
                var idx = (int)Math.Ceiling(populated * 0.95) - 1;
                if (idx < 0) idx = 0;
                return TimeSpan.FromTicks(snapshot[idx]);
            }
        }
    }
}

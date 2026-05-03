namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// Allocation-free ring-buffer rolling-window sampler over (timestamp, inUse, total).
/// Sampled at most once per <c>debounce</c> (1 s) regardless of caller frequency to keep the hot
/// Acquire/Release path effectively free. <see cref="AverageUtilization"/> is read-only and lock-free.
/// </summary>
internal sealed class UtilizationSampler
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly long[] _bucketTimestampsTicks;  // -1 = empty
    private readonly int[] _bucketInUse;
    private readonly int[] _bucketTotal;
    private int _writeIndex;
    private long _lastSampleTicks;        // 1s debounce per RESEARCH OQ #3
    private readonly long _debounceTicks; // = TimeSpan.FromSeconds(1).Ticks

    public UtilizationSampler(TimeProvider time, TimeSpan window, TimeSpan bucketSize)
    {
        _time = time;
        _window = window;
        var n = Math.Max(1, (int)Math.Ceiling(window.TotalSeconds / bucketSize.TotalSeconds));
        _bucketTimestampsTicks = new long[n];
        Array.Fill(_bucketTimestampsTicks, -1L);
        _bucketInUse = new int[n];
        _bucketTotal = new int[n];
        _debounceTicks = TimeSpan.FromSeconds(1).Ticks;
    }

    public void Sample(int inUse, int total)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var last = Volatile.Read(ref _lastSampleTicks);
        if (nowTicks - last < _debounceTicks) return;
        if (Interlocked.CompareExchange(ref _lastSampleTicks, nowTicks, last) != last) return;
        var idx = (int)((uint)Interlocked.Increment(ref _writeIndex) - 1) % _bucketTimestampsTicks.Length;
        // CR-02 fix: publication pattern — write data fields FIRST (plain stores), then publish
        // the timestamp LAST with a store-release fence. Reader's Volatile.Read on the timestamp
        // is paired load-acquire and guarantees visibility of the data fields written before it.
        // The previous order (timestamp first, data second) only fenced stores PRIOR to the
        // timestamp write — leaving _bucketInUse/_bucketTotal stores unfenced, observable as
        // stale on weakly-ordered architectures (ARM64).
        _bucketInUse[idx] = inUse;
        _bucketTotal[idx] = total;
        Volatile.Write(ref _bucketTimestampsTicks[idx], nowTicks);
    }

    public double AverageUtilization()
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var cutoffTicks = nowTicks - _window.Ticks;
        long sumInUse = 0, sumTotal = 0;
        for (int i = 0; i < _bucketTimestampsTicks.Length; i++)
        {
            var ts = Volatile.Read(ref _bucketTimestampsTicks[i]);
            if (ts < 0 || ts < cutoffTicks) continue;
            sumInUse += _bucketInUse[i];
            sumTotal += _bucketTotal[i];
        }
        return sumTotal == 0 ? 0.0 : (double)sumInUse / sumTotal;
    }
}

namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// Exponential backoff state machine for the background sweeper. After 3 consecutive
/// failure windows (>= 50% of items checked are unhealthy) the sweep interval doubles
/// up to <c>maxBackoff</c>; the first clean window resets to <c>baseInterval</c>.
/// </summary>
internal sealed class SweepBackoffState
{
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxBackoff;
    private int _consecutiveFailureWindows;
    private TimeSpan _currentInterval;

    public TimeSpan CurrentInterval => _currentInterval;
    public TimeSpan PreviousInterval { get; private set; }
    public bool IntervalChanged { get; private set; }
    public int ConsecutiveFailureWindows => _consecutiveFailureWindows;

    public SweepBackoffState(TimeSpan baseInterval, TimeSpan maxBackoff)
    {
        _baseInterval = baseInterval;
        _maxBackoff = maxBackoff;
        _currentInterval = baseInterval;
        PreviousInterval = baseInterval;
    }

    public void OnSweepResult(int totalChecked, int unhealthy)
    {
        var prev = _currentInterval;
        if (totalChecked == 0)
        {
            // No items checked this tick — neither bump nor reset; keep existing interval.
            IntervalChanged = false;
            PreviousInterval = prev;
            return;
        }
        if (unhealthy * 2 >= totalChecked)
        {
            _consecutiveFailureWindows++;
            if (_consecutiveFailureWindows >= 3)
            {
                var doubled = TimeSpan.FromTicks(_currentInterval.Ticks * 2);
                _currentInterval = doubled > _maxBackoff ? _maxBackoff : doubled;
            }
        }
        else
        {
            _consecutiveFailureWindows = 0;
            _currentInterval = _baseInterval;
        }
        PreviousInterval = prev;
        IntervalChanged = _currentInterval != prev;
    }
}

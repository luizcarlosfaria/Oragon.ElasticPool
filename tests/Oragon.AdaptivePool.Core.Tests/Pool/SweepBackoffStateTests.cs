using AwesomeAssertions;
using Oragon.AdaptivePool.Core.Internals;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.Pool;

public class SweepBackoffStateTests
{
    private static SweepBackoffState NewState() =>
        new(baseInterval: TimeSpan.FromSeconds(30), maxBackoff: TimeSpan.FromMinutes(5));

    [Fact]
    public void OnSweepResult_TotalCheckedZero_DoesNotChangeInterval()
    {
        var s = NewState();
        var initial = s.CurrentInterval;

        s.OnSweepResult(totalChecked: 0, unhealthy: 0);

        s.CurrentInterval.Should().Be(initial);
        s.IntervalChanged.Should().BeFalse();
    }

    [Fact]
    public void OnSweepResult_OneCleanWindow_AfterFailure_ResetsToBase()
    {
        var s = NewState();

        // 2 failure windows but not yet enough to bump (needs 3).
        s.OnSweepResult(totalChecked: 4, unhealthy: 4);
        s.OnSweepResult(totalChecked: 4, unhealthy: 4);
        s.ConsecutiveFailureWindows.Should().Be(2);

        // Clean tick — counter resets, interval back to base.
        s.OnSweepResult(totalChecked: 4, unhealthy: 0);

        s.CurrentInterval.Should().Be(TimeSpan.FromSeconds(30));
        s.ConsecutiveFailureWindows.Should().Be(0);
    }

    [Fact]
    public void OnSweepResult_ThreeFailureWindows_DoublesInterval()
    {
        var s = NewState();

        s.OnSweepResult(4, 4);
        s.OnSweepResult(4, 4);
        s.OnSweepResult(4, 4);

        s.CurrentInterval.Should().Be(TimeSpan.FromSeconds(60));
        s.IntervalChanged.Should().BeTrue();
        s.ConsecutiveFailureWindows.Should().Be(3);
    }

    [Fact]
    public void OnSweepResult_FurtherFailures_ContinueDoubling_UpToCap()
    {
        var s = NewState();

        // 3 -> 60s, 4 -> 120s, 5 -> 240s, 6 -> 300s (capped)
        s.OnSweepResult(4, 4); // 1
        s.OnSweepResult(4, 4); // 2
        s.OnSweepResult(4, 4); // 3 -> 60s
        s.CurrentInterval.Should().Be(TimeSpan.FromSeconds(60));
        s.OnSweepResult(4, 4); // 4 -> 120s
        s.CurrentInterval.Should().Be(TimeSpan.FromSeconds(120));
        s.OnSweepResult(4, 4); // 5 -> 240s
        s.CurrentInterval.Should().Be(TimeSpan.FromSeconds(240));
        s.OnSweepResult(4, 4); // 6 -> 480s but capped at 300s
        s.CurrentInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void OnSweepResult_AfterCap_RemainsAtCap()
    {
        var s = NewState();

        // Drive to cap.
        for (int i = 0; i < 10; i++) s.OnSweepResult(4, 4);
        s.CurrentInterval.Should().Be(TimeSpan.FromMinutes(5));

        // Continue failing — no further change.
        for (int i = 0; i < 100; i++) s.OnSweepResult(4, 4);
        s.CurrentInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void IntervalChanged_FlagSetCorrectly()
    {
        var s = NewState();

        s.OnSweepResult(4, 4); s.IntervalChanged.Should().BeFalse(); // 1st failure window — not bumped yet
        s.OnSweepResult(4, 4); s.IntervalChanged.Should().BeFalse(); // 2nd
        s.OnSweepResult(4, 4); s.IntervalChanged.Should().BeTrue();  // 3rd → bumped
        s.OnSweepResult(4, 4); s.IntervalChanged.Should().BeTrue();  // 4th → still doubling
        s.OnSweepResult(4, 0); s.IntervalChanged.Should().BeTrue();  // clean tick → reset to base, that's a change
        s.OnSweepResult(4, 0); s.IntervalChanged.Should().BeFalse(); // already at base, no change
    }

    [Fact]
    public void OnSweepResult_HalfUnhealthy_TriggersFailureWindow()
    {
        var s = NewState();

        // unhealthy * 2 >= totalChecked  -> 2 >= 4? no. So 2/4 unhealthy is exactly the boundary: 2*2 = 4 >= 4 -> yes
        s.OnSweepResult(totalChecked: 4, unhealthy: 2);
        s.ConsecutiveFailureWindows.Should().Be(1);

        // 1/4 unhealthy: 1*2 = 2 < 4 -> reset
        s.OnSweepResult(totalChecked: 4, unhealthy: 1);
        s.ConsecutiveFailureWindows.Should().Be(0);
    }
}

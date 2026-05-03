using AwesomeAssertions;
using Xunit;

namespace Oragon.AdaptivePool.Core.Stress;

public class PlaceholderStressFact
{
    [Fact]
    public void StressProject_IsWired()
    {
        // Proves stress project compiles and runs in isolation.
        // Plan 03 replaces this with the MaxSize=1 ping-pong stress test.
        true.Should().BeTrue();
    }
}

using AwesomeAssertions;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests;

public class PlaceholderSmokeTest
{
    [Fact]
    public void Toolchain_IsWired()
    {
        // Proves: xUnit v3 + MTP + AwesomeAssertions + Core ProjectReference all link.
        // Will be deleted by Plan 02 once real tests exist.
        true.Should().BeTrue();
    }
}

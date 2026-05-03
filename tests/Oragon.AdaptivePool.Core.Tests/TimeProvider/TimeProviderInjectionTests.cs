using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Oragon.AdaptivePool.Core.Builder;
using Oragon.AdaptivePool.Core.Tests.TestSupport;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests.TimeProvider;

public class TimeProviderInjectionTests
{
    [Fact]
    public async Task WithTimeProvider_FakeTimeProvider_BuilderAcceptsAndPoolBuilds()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sp = new ServiceCollection().BuildServiceProvider();

        await using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(0, 1, 1)
            .WithTimeProvider(fake)
            .Build();

        await pool.ReadyAsync();

        await using var item = await pool.AcquireAsync();
        item.Value.Should().NotBeNull();
    }

    [Fact]
    public void WithTimeProvider_DefaultsToSystem_WhenNotSet()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        // Builder defaults TimeProvider to System; pool should build without explicit WithTimeProvider().
        Action act = () =>
        {
            using var pool = AdaptiveObjectPoolFactory.Build<Resource>(sp)
                .Factory((s, ct) => ValueTask.FromResult(new Resource()))
                .WithBounds(0, 1, 0)
                .Build();
        };

        act.Should().NotThrow();
    }
}

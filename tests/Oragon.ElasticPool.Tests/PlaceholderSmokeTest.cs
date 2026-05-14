using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.DependencyInjection;
using Xunit;

namespace Oragon.ElasticPool.Tests;

/// <summary>
/// Always-on integration smoke covering the full DI + Acquire + Dispose roundtrip.
/// Replaces Plan 01's trivial placeholder; proves the surface from <see cref="ServiceCollectionExtensions.AddElasticPool{T}"/>
/// down to the engine's idle-queue accounting works end-to-end.
/// </summary>
public class PoolSmokeTests
{
    private sealed class Resource { }

    [Fact]
    public async Task DI_Build_Acquire_Dispose_Roundtrip_Works()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddElasticPool<Resource>("smoke", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 0));
        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>("smoke");

        await using (var item = await pool.AcquireAsync())
        {
            item.Value.Should().NotBeNull();
            pool.InUse.Should().Be(1);
        }
        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
    }
}

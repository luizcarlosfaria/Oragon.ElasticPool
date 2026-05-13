using Testcontainers.RabbitMq;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit v3 IClassFixture wrapping a Testcontainers-managed
/// <c>rabbitmq:4.0-management</c> container. One broker per test class — fast enough
/// for our integration suite and preserves test isolation by class.
/// <see cref="IAsyncLifetime"/> returns <see cref="ValueTask"/> per xUnit v3 contract.
/// </summary>
/// <remarks>
/// The image tag is pinned to the <c>4.0</c> minor stream (not the floating
/// <c>4-management</c> major tag) for build reproducibility (IN-03). Bump on
/// regular dependency review cycles.
/// </remarks>
public class RabbitMqContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// Pinned RabbitMQ image. See class remarks for rationale.
    /// Bump in lockstep with <see cref="LowChannelMaxFixture"/>.
    /// </summary>
    internal const string ImageTag = "rabbitmq:4.0-management";

    /// <summary>
    /// Static initializer disables Testcontainers' Ryuk (ResourceReaper) globally for
    /// this assembly. Ryuk spawns a privileged sidecar container that races on init
    /// when multiple fixtures across multi-TFM test runs start concurrently, causing
    /// intermittent <c>ResourceReaperException: Initialization has been cancelled</c>.
    /// We rely on <see cref="DisposeAsync"/> for deterministic cleanup; if the test
    /// process aborts uncleanly, the user can prune dangling containers manually.
    /// </summary>
    static RabbitMqContainerFixture()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder(ImageTag)
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public virtual async ValueTask InitializeAsync() => await Container.StartAsync();

    public virtual async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

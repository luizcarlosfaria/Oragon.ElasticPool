using Testcontainers.RabbitMq;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// Variant fixture that constrains the broker to <c>channel_max=10</c>. This is the
/// anchor fixture for the eager-spread integration test (<see cref="ChannelSpreadIntegrationTests"/>):
/// when the channel pool's <c>MaxChannelsPerConnection</c> matches the broker's
/// <c>channel_max</c>, requesting more than 10 simultaneous channels MUST cause the
/// adapter to spread across multiple connections.
/// </summary>
/// <remarks>
/// Configuration: passes <c>RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS</c> with
/// <c>-rabbit channel_max 10</c>. This is simpler than mounting a config file and avoids
/// the test creating filesystem artifacts (T-03-15 mitigation).
/// </remarks>
public sealed class LowChannelMaxFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder("rabbitmq:4-management")
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", "-rabbit channel_max 10")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async ValueTask InitializeAsync() => await Container.StartAsync();

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

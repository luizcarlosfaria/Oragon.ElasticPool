using Testcontainers.RabbitMq;
using Xunit;

namespace Oragon.AdaptivePool.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit v3 IClassFixture wrapping a Testcontainers-managed <c>rabbitmq:4-management</c>
/// container. One broker per test class — fast enough for our integration suite and
/// preserves test isolation by class. <see cref="IAsyncLifetime"/> returns
/// <see cref="ValueTask"/> per xUnit v3 contract.
/// </summary>
public class RabbitMqContainerFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder("rabbitmq:4-management")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public virtual async ValueTask InitializeAsync() => await Container.StartAsync();

    public virtual async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

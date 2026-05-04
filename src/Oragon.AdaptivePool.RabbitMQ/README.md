# Oragon.AdaptivePool.RabbitMQ

[![NuGet](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)

> RabbitMQ.Client v7+ adapter for [`Oragon.AdaptivePool.Core`](https://www.nuget.org/packages/Oragon.AdaptivePool.Core).
> Layered, lifecycle-managed pools for `IConnection` and `IChannel`. Async-first.

## Install

```bash
dotnet add package Oragon.AdaptivePool.RabbitMQ
```

(`Oragon.AdaptivePool.Core` is pulled transitively.)

## What it gives you

- `services.AddAdaptiveConnectionPool(name, configureFactory, configurePool)` — pool of `IConnection` with `IsOpen`-based health checks.
- `services.AddAdaptiveChannelPool(name, connectionPoolName, configurePool)` — pool of `IChannel` **layered** on the connection pool: channels share retained connection leases up to `MaxChannelsPerConnection`.
- Conventions consistent with [`Oragon.RabbitMQ`](https://github.com/oragon/Oragon.RabbitMQ) (sister consumer-side library).

## Quickstart — bursty publisher

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// Connection pool
builder.Services.AddAdaptiveConnectionPool(
    name: "default",
    configureFactory: f =>
    {
        f.Uri = new Uri("amqp://guest:guest@localhost:5672/");
        // AutomaticRecoveryEnabled is overridden to FALSE by the pool — see callout below.
    },
    configurePool: pool =>
    {
        pool.WithBounds(minSize: 1, maxSize: 32, initialSize: 2);
        pool.IdleTimeout(TimeSpan.FromMinutes(2));
        // Optional for demos or faster recycle loops:
        // pool.WithSweepInterval(TimeSpan.FromSeconds(5));
        // pool.WithShrinkOnUtilizationPercent(0.60);
        // pool.WithShrinkTargetUtilizationPercent(0.75);
        // pool.WithShrinkBatchSize(8);
        // pool.WithShrinkCooldownWindows(1);
    });

// Channel pool layered on top
builder.Services.AddAdaptiveChannelPool(
    name: "default",
    connectionPoolName: "default",
    configurePool: pool =>
    {
        pool.WithBounds(minSize: 0, maxSize: 256, initialSize: 0);
        pool.IdleTimeout(TimeSpan.FromSeconds(30));
    });

using var host = builder.Build();
// AddAdaptiveChannelPool registers a *keyed* singleton — match the name above.
var channels = host.Services.GetRequiredKeyedService<IAdaptivePool<IChannel>>("default");

// Publish under any load shape — pool grows/shrinks/heals automatically.
// IChannel is NOT thread-safe — acquire one per logical publisher / per iteration.
await using var lease = await channels.AcquireAsync();
var props = new BasicProperties();
await lease.Value.BasicPublishAsync(exchange: "", routingKey: "demo", mandatory: false,
                                    basicProperties: props, body: "hello"u8.ToArray());
```

## ⚠ Channel-per-publisher discipline

`IChannel` instances are **not thread-safe** for concurrent publishes. Acquire one
channel per logical publisher (or per iteration in a loop) and dispose the lease
before sharing across threads. The sample (`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`)
demonstrates the per-iteration acquire pattern under bursty load.

## ⚠ Sizing the connection pool

The channel pool shares each retained `IConnection` lease across multiple channels
until `MaxChannelsPerConnection` is reached. Size the connection pool for roughly:

```text
ceil(peak live channels / MaxChannelsPerConnection)
```

For example, 64 live channels with `MaxChannelsPerConnection(16)` should need about
4 retained connections, not 64. If you see waiters parked indefinitely on
`chPool.AcquireAsync`, check both the channel pool's `MaxSize` and the connection
pool's `MaxSize`.

## Shrink behavior

The underlying core shrinks on sustained aggregate low pressure, not on per-item
age. A pool is eligible when there are no waiters, utilization is at or below
`ShrinkOnUtilizationPercent`, excess items are available, the post-grow cooldown
has elapsed, and that low-pressure shape has lasted for `IdleTimeout`. The target
size is computed from `ShrinkTargetUtilizationPercent`, and `ShrinkBatchSize`
controls how quickly each sweep moves toward that target.

## ⚠ AutomaticRecoveryEnabled override

`Oragon.AdaptivePool.RabbitMQ` forces `ConnectionFactory.AutomaticRecoveryEnabled = false`
to avoid two recovery loops (RabbitMQ.Client's vs. the pool's). The pool owns the
lifecycle: `BeforeUse` and `Check` hooks consult `IConnection.IsOpen` and
`Release` calls `CloseAsync()`. If you configured `AutomaticRecoveryEnabled = true`
on the factory, you'll see a single warning log on first acquire (EventId 2001):

> `AutomaticRecoveryEnabled was true on the configured ConnectionFactory for pool 'X'; Oragon.AdaptivePool overrides this to false (the pool owns lifecycle).`

This is intentional. To suppress, set `AutomaticRecoveryEnabled = false` yourself.

## Telemetry

Inherits the Core `Meter` and `ActivitySource` (both named `"Oragon.AdaptivePool"`).
The connection and channel pools are independently named (`pool.name` tag) so you
can chart them separately. See [the Core README](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
for the full instrument inventory.

## Sample: end-to-end bursty publisher

[`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](https://github.com/oragon/Oragon.AdaptivePool/tree/main/samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher)
ships a runnable demo: cycles between idle and 100k-simultaneous publish,
demonstrates pool grow/shrink/heal under real load against a Testcontainers
RabbitMQ broker.

[`samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard`](https://github.com/oragon/Oragon.AdaptivePool/tree/main/samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard)
ships an Aspire + Blazor dashboard for visual validation: RabbitMQ starts from the
AppHost, the page updates at 10 Hz, and a concurrency slider lets you grow and
shrink the pools live.

```bash
RABBITMQ_URI=amqp://guest:guest@localhost:5672/ \
  dotnet run --project samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher
```

## Compatibility

- `RabbitMQ.Client` 7.x (async-first API; `IChannel` replaces `IModel`).
- Multi-target `net10.0` / `net9.0` / `net8.0`.

## License

[MIT](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)

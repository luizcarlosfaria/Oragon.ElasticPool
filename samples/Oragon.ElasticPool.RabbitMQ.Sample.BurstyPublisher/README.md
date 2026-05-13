# Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher

Runnable end-to-end demonstration of **Oragon.ElasticPool**'s headline value
proposition: an `IChannel` pool layered over an `IConnection` pool that **grows
under burst, shrinks during idle, and regrows on the next burst** — without leaks,
without sharing channels across publishing threads (Pitfall 10), and with the
`AutomaticRecoveryEnabled` lifecycle override applied automatically.

## What this demonstrates

A `BackgroundService` (`BurstyPublisherWorker`) cycles **idle → burst → idle** three
times. Each burst publishes 100,000 persistent messages with a parallelism of 256,
acquiring a fresh `IChannel` per iteration via the adaptive channel pool. Between
bursts, the pool's idle-sweep discards stale channels/connections — observable via
the meter `Oragon.ElasticPool` and the activity-source `Oragon.ElasticPool`.

This is the manual, full-scale counterpart to the scaled-down
`BurstyPublisherIntegrationTests` in CI (~600 publishes × 3 cycles, ~12 s wall-clock).

## Prerequisites

- .NET 10 SDK
- Docker (for the local RabbitMQ broker)

## Run

1. Start a RabbitMQ 4 broker locally:

   ```bash
   docker run -d --rm --name rabbit-sample \
     -p 5672:5672 -p 15672:15672 \
     rabbitmq:4-management
   ```

   (Management UI: <http://localhost:15672>, default `guest/guest`.)

2. Run the sample:

   ```bash
   dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
   ```

3. Optionally point at a remote broker:

   ```bash
   RABBITMQ_URI=amqp://user:pass@host:5672/vhost dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
   ```

## Tunable env vars

For development convenience the cycle dimensions are env-var-tunable. Defaults match
CONTEXT.md (3 cycles, 5 min idle, 100 k publishes per burst, parallelism 256):

| Variable             | Default | Description                       |
|----------------------|---------|-----------------------------------|
| `BURSTY_CYCLES`      | 3       | Number of idle→burst→idle cycles  |
| `BURSTY_IDLE_SECONDS`| 300     | Idle gap between bursts (seconds) |
| `BURSTY_BURST_COUNT` | 100000  | Messages per burst                |
| `BURSTY_PARALLELISM` | 256     | Parallel publishers in each burst |

For a quick smoke run (~30 s wall-clock):

```bash
BURSTY_CYCLES=1 BURSTY_IDLE_SECONDS=2 BURSTY_BURST_COUNT=1000 BURSTY_PARALLELISM=32 \
  dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
```

## Expected log output

```
14:32:11  info: Topology declared: exchange=oragon.elasticpool.sample, queue=oragon.elasticpool.sample.queue, routingKey=bursty.demo
14:32:11  info: Cycle 1/3: idle for 300s
14:37:11  info: Cycle 1/3: burst 100000 publishes (parallelism=256)
14:37:43  info: Cycle 1/3: burst complete in 31487 ms (3175 msg/s)
14:37:43  info: Cycle 2/3: idle for 300s
14:42:43  info: Cycle 2/3: burst 100000 publishes (parallelism=256)
14:43:14  info: Cycle 2/3: burst complete in 30912 ms (3234 msg/s)
...
```

Throughput numbers vary by hardware. The meaningful observation is that the second
burst reaches steady throughput **faster** than the first — because some pool items
remain warm (not all channels are torn down during idle, only those past
`IdleTimeout=60s`). This is the elasticity payoff.

## Observability via OpenTelemetry

The pool exposes a `Meter` and `ActivitySource` named `Oragon.ElasticPool`. To
observe pool growth/shrink in real time, wire them into your OTel pipeline:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Oragon.ElasticPool"))
    .WithTracing(t => t.AddSource("Oragon.ElasticPool"));
```

Available metrics include `pool.grow.count`, `pool.shrink.count`,
`pool.health.failures`, `pool.size` (gauge), and acquire/release durations.

## Safety note (T-03-13 mitigation)

The default `BURSTY_PARALLELISM=256` will saturate a developer laptop's network
stack and CPU during the burst window. **Reduce it on your first run** to size your
machine: `BURSTY_PARALLELISM=32` is a sane starting point; raise gradually.

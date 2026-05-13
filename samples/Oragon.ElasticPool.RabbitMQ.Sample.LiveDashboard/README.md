# Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard

Aspire + Blazor dashboard for visually validating `Oragon.ElasticPool.RabbitMQ`.
The AppHost starts RabbitMQ, the Web app publishes adjustable load, and the page
refreshes pool state every 100 ms.

## What this demonstrates

- Connection and channel pools grow when publish concurrency increases.
- Pools shrink after aggregate pressure drops (`IdleTimeout=3s`, `SweepInterval=1s`,
  pressure threshold 60%, target utilization 75%).
- `IElasticPool<T>.Total`, `Available`, `InUse`, and `Waiting` can be read directly
  for operational views.
- OpenTelemetry gauges `pool.size`, `pool.available`, `pool.in_use`, and
  `pool.waiting` are exported through Aspire service defaults.

## Run

Prerequisites:

- .NET 10 SDK
- Docker or another Aspire-supported container runtime

```bash
dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.AppHost
```

Open the Aspire dashboard URL printed by the AppHost, then open the `web` resource.
Use the concurrency slider:

1. Set concurrency to `8` or `16` and watch channels/connections grow.
2. Raise it toward `200` to create pressure.
3. Reduce it to `30` or `0` and watch available entries shrink over the next few sweep ticks.

## Project layout

- `*.AppHost` starts RabbitMQ and the Web frontend.
- `*.ServiceDefaults` wires Aspire health checks and OpenTelemetry, including the
  `Oragon.ElasticPool` meter/source.
- `*.Web` contains the Blazor UI and `LiveLoadController`.

This sample is intentionally isolated in its own solution so it does not change the
main repository build/test cadence.

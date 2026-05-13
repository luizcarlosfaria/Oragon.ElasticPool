// Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
//
// Headline scenario for Oragon.ElasticPool: a BackgroundService cycling
// idle → 100k-publish burst → idle, demonstrating the pool growing under burst,
// shrinking during idle, and regrowing on the next burst — without leaks.
//
// Consumers wishing to observe via OpenTelemetry can attach:
//   .AddMeter("Oragon.ElasticPool")
//   .AddSource("Oragon.ElasticPool")
// to their OTel pipeline (the meter + activity-source names are conventions).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oragon.ElasticPool.RabbitMQ.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Connection string source: env var RABBITMQ_URI (default points at local docker broker).
var connectionString = Environment.GetEnvironmentVariable("RABBITMQ_URI")
                       ?? "amqp://guest:guest@localhost:5672/";

builder.Services.AddElasticConnectionPool(
    name: "sample",
    configureFactory: cf => cf.Uri = new Uri(connectionString),
    configurePool: p => p.WithBounds(min: 1, max: 32, initial: 1));

builder.Services.AddElasticChannelPool(
    name: "sample",
    connectionPoolName: "sample",
    configurePool: p => p
        .WithBounds(min: 0, max: 64, initial: 0)
        .WithMaxChannelsPerConnection(50));

builder.Services.AddHostedService<BurstyPublisherWorker>();

var host = builder.Build();
await host.RunAsync();

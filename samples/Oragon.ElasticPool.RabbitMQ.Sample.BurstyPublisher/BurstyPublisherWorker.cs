using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oragon.ElasticPool.Abstractions;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher;

/// <summary>
/// BackgroundService cycling idle → burst → idle, demonstrating the headline value
/// proposition of <c>Oragon.ElasticPool</c>: pool grows under burst, shrinks during
/// idle, regrows on next burst — without leaks.
/// </summary>
/// <remarks>
/// Defaults match CONTEXT.md (3 cycles, 5min idle, 100k publishes per burst). All
/// dimensions are env-var-tunable for development convenience.
/// </remarks>
public sealed class BurstyPublisherWorker(
    [FromKeyedServices("sample")] IElasticPool<IChannel> channelPool,
    ILogger<BurstyPublisherWorker> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string Exchange = "oragon.elasticpool.sample";
    private const string Queue = "oragon.elasticpool.sample.queue";
    private const string RoutingKey = "bursty.demo";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cycles = ParseInt(Environment.GetEnvironmentVariable("BURSTY_CYCLES"), 3);
        var idleSeconds = ParseInt(Environment.GetEnvironmentVariable("BURSTY_IDLE_SECONDS"), 300);
        var burstCount = ParseInt(Environment.GetEnvironmentVariable("BURSTY_BURST_COUNT"), 100_000);
        var parallelism = ParseInt(Environment.GetEnvironmentVariable("BURSTY_PARALLELISM"), 256);

        try
        {
            await DeclareTopologyAsync(stoppingToken);

            for (var cycle = 0; cycle < cycles && !stoppingToken.IsCancellationRequested; cycle++)
            {
                logger.LogInformation("Cycle {Cycle}/{Cycles}: idle for {IdleSeconds}s", cycle + 1, cycles, idleSeconds);
                await Task.Delay(TimeSpan.FromSeconds(idleSeconds), stoppingToken);

                logger.LogInformation("Cycle {Cycle}/{Cycles}: burst {Count} publishes (parallelism={Parallelism})", cycle + 1, cycles, burstCount, parallelism);
                var sw = Stopwatch.StartNew();

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, burstCount),
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stoppingToken },
                    async (i, token) =>
                    {
                        // Pitfall 10: MUST acquire a fresh channel per iteration —
                        // IChannel is NOT thread-safe for publish (RabbitMQ.Client docs).
                        await using var lease = await channelPool.AcquireAsync(token);
                        var body = JsonSerializer.SerializeToUtf8Bytes(new { Idx = i, Cycle = cycle });
                        await lease.Value.BasicPublishAsync(
                            exchange: Exchange,
                            routingKey: RoutingKey,
                            mandatory: false,
                            basicProperties: new BasicProperties { Persistent = true },
                            body: body,
                            cancellationToken: token);
                    });

                sw.Stop();
                var throughput = burstCount / sw.Elapsed.TotalSeconds;
                logger.LogInformation("Cycle {Cycle}/{Cycles}: burst complete in {ElapsedMs} ms ({Throughput:F0} msg/s)", cycle + 1, cycles, sw.ElapsedMilliseconds, throughput);
            }

            // Final idle window so the user can observe shrink in metrics dashboards.
            logger.LogInformation("All cycles complete; idle for {IdleSeconds}s before shutdown", idleSeconds);
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("BurstyPublisherWorker cancelled cleanly");
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task DeclareTopologyAsync(CancellationToken ct)
    {
        await using var lease = await channelPool.AcquireAsync(ct);
        await lease.Value.ExchangeDeclareAsync(
            exchange: Exchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);
        await lease.Value.QueueDeclareAsync(
            queue: Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: ct);
        await lease.Value.QueueBindAsync(
            queue: Queue,
            exchange: Exchange,
            routingKey: RoutingKey,
            cancellationToken: ct);
        logger.LogInformation("Topology declared: exchange={Exchange}, queue={Queue}, routingKey={RoutingKey}",
            Exchange, Queue, RoutingKey);
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var v) ? v : fallback;
}

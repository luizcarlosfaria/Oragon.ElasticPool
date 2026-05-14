using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Abstractions;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.Web.Live;

public sealed class LiveLoadController(
    [FromKeyedServices(LivePoolNames.PoolName)] IElasticPool<IConnection> connectionPool,
    [FromKeyedServices(LivePoolNames.PoolName)] IElasticPool<IChannel> channelPool,
    ILogger<LiveLoadController> logger) : BackgroundService
{
    private const int MaxConcurrency = 200;
    private const string Exchange = "oragon.elasticpool.live";
    private static readonly ReadOnlyMemory<byte> Body = Encoding.UTF8.GetBytes("oragon-elasticpool-live");

    private readonly object _gate = new();
    private readonly object _rateGate = new();
    private readonly List<WorkerSlot> _workers = [];
    private CancellationToken _stoppingToken;
    private bool _started;
    private int _targetConcurrency;
    private long _published;
    private long _errors;
    private long _lastPublished;
    private DateTimeOffset _lastRateAt = DateTimeOffset.UtcNow;
    private double _publishRate;
    private string _status = "Starting";
    private string? _lastError;

    public void SetTargetConcurrency(int value)
    {
        var target = Math.Clamp(value, 0, MaxConcurrency);
        lock (_gate)
        {
            _targetConcurrency = target;
            ReconcileWorkersLocked();
        }
    }

    public void Stop() => SetTargetConcurrency(0);

    public LiveDashboardSnapshot GetSnapshot()
    {
        var published = Interlocked.Read(ref _published);
        var errors = Interlocked.Read(ref _errors);
        var now = DateTimeOffset.UtcNow;

        lock (_rateGate)
        {
            var elapsed = (now - _lastRateAt).TotalSeconds;
            if (elapsed >= 0.05)
            {
                _publishRate = (published - _lastPublished) / elapsed;
                _lastPublished = published;
                _lastRateAt = now;
            }
        }

        return new LiveDashboardSnapshot(
            Snapshot(connectionPool),
            Snapshot(channelPool),
            Volatile.Read(ref _targetConcurrency),
            published,
            errors,
            _publishRate,
            _status,
            _lastError);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            await DeclareTopologyAsync(stoppingToken).ConfigureAwait(false);
            lock (_gate)
            {
                _started = true;
                _status = "Idle";
                ReconcileWorkersLocked();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _status = "Startup failed";
            logger.LogError(ex, "Live dashboard startup failed");
        }
        finally
        {
            StopAllWorkers();
        }
    }

    private async Task DeclareTopologyAsync(CancellationToken ct)
    {
        await using var lease = await channelPool.AcquireAsync(ct).ConfigureAwait(false);
        await lease.Value.ExchangeDeclareAsync(
            exchange: Exchange,
            type: ExchangeType.Fanout,
            durable: false,
            autoDelete: true,
            cancellationToken: ct).ConfigureAwait(false);
    }

    private void ReconcileWorkersLocked()
    {
        if (!_started)
        {
            _status = _targetConcurrency == 0 ? "Starting" : "Starting; load queued";
            return;
        }

        _workers.RemoveAll(w => w.Task.IsCompleted);

        while (_workers.Count > _targetConcurrency)
        {
            var last = _workers[^1];
            _workers.RemoveAt(_workers.Count - 1);
            last.Cancellation.Cancel();
        }

        while (_workers.Count < _targetConcurrency)
        {
            var workerNumber = _workers.Count + 1;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            var task = Task.Run(async () =>
            {
                try
                {
                    await PublishLoopAsync(workerNumber, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    cts.Dispose();
                }
            }, CancellationToken.None);
            _workers.Add(new WorkerSlot(cts, task));
        }

        _status = _targetConcurrency == 0 ? "Idle" : $"Publishing with {_targetConcurrency} workers";
    }

    private async Task PublishLoopAsync(int workerNumber, CancellationToken ct)
    {
        await using var lease = await channelPool.AcquireAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await lease.Value.BasicPublishAsync(
                    exchange: Exchange,
                    routingKey: string.Empty,
                    mandatory: false,
                    basicProperties: new BasicProperties(),
                    body: Body,
                    cancellationToken: ct).ConfigureAwait(false);
                Interlocked.Increment(ref _published);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                _lastError = ex.Message;
                logger.LogWarning(ex, "Worker {WorkerNumber} publish failed", workerNumber);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private void StopAllWorkers()
    {
        lock (_gate)
        {
            foreach (var worker in _workers)
            {
                worker.Cancellation.Cancel();
            }
            _workers.Clear();
            _targetConcurrency = 0;
            _status = "Stopped";
        }
    }

    private static PoolSnapshot Snapshot<T>(IElasticPool<T> pool)
        where T : notnull =>
        new(pool.Total, pool.Available, pool.InUse, pool.Waiting, pool.MinSize, pool.MaxSize);

    private sealed record WorkerSlot(CancellationTokenSource Cancellation, Task Task);
}

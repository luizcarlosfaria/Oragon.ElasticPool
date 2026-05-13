namespace Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.Web.Live;

public sealed record PoolSnapshot(
    int Total,
    int Available,
    int InUse,
    int Waiting,
    int MinSize,
    int MaxSize);

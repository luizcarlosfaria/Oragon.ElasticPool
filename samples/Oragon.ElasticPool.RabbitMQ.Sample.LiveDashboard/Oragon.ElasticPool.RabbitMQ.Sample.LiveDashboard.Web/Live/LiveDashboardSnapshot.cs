namespace Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.Web.Live;

public sealed record LiveDashboardSnapshot(
    PoolSnapshot Connections,
    PoolSnapshot Channels,
    int TargetConcurrency,
    long Published,
    long Errors,
    double PublishRate,
    string Status,
    string? LastError);

namespace Oragon.ElasticPool.Builder;

public static class ElasticObjectPoolFactory
{
    public static ElasticPoolBuilder<T> Build<T>(IServiceProvider services, CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ElasticPoolBuilder<T>(services, cancellationToken);
    }
}

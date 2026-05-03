namespace Oragon.AdaptivePool.Core.Builder;

public static class AdaptiveObjectPoolFactory
{
    public static AdaptivePoolBuilder<T> Build<T>(IServiceProvider services, CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        return new AdaptivePoolBuilder<T>(services, cancellationToken);
    }
}

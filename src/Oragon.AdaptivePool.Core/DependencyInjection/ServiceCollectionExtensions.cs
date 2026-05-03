using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Builder;

namespace Oragon.AdaptivePool.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAdaptivePool{T}"/> in DI under the given <paramref name="name"/>.
    /// Use <c>string.Empty</c> for single-pool apps (also resolvable as non-keyed <c>IAdaptivePool&lt;T&gt;</c>).
    /// Multiple pools of the same T can coexist by name.
    /// </summary>
    public static IServiceCollection AddAdaptivePool<T>(
        this IServiceCollection services,
        string name,
        Action<AdaptivePoolBuilder<T>> configure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AdaptivePoolBuilderConfigurator<T>>(name)
                .Configure(c => c.Configure = configure);

        services.TryAddKeyedSingleton<IAdaptivePool<T>>(name, (sp, key) =>
        {
            var keyName = (string)key!;
            var configurator = sp.GetRequiredService<IOptionsMonitor<AdaptivePoolBuilderConfigurator<T>>>().Get(keyName);
            // Optional: pull application-stopping CT if the host registers it (no hard dep).
            var lifetimeCt = TryGetHostApplicationStoppingToken(sp);
            var builder = AdaptiveObjectPoolFactory.Build<T>(sp, lifetimeCt);
            builder.WithName(keyName);  // internal method, same assembly
            configurator.Configure(builder);
            return builder.Build();
        });

        if (name.Length == 0)
        {
            services.TryAddSingleton<IAdaptivePool<T>>(sp => sp.GetRequiredKeyedService<IAdaptivePool<T>>(string.Empty));
        }

        return services;
    }

    private static CancellationToken TryGetHostApplicationStoppingToken(IServiceProvider sp)
    {
        // Resolve IHostApplicationLifetime by name without a Hosting.Abstractions reference.
        // We probe for any registered service exposing an "ApplicationStopping" CancellationToken property.
        var candidate = sp.GetServices<object>()
            .FirstOrDefault(s => s?.GetType().Name == "IHostApplicationLifetime");
        if (candidate is null) return CancellationToken.None;
        var prop = candidate.GetType().GetProperty("ApplicationStopping");
        if (prop?.GetValue(candidate) is CancellationToken ct) return ct;
        return CancellationToken.None;
    }

    private sealed class AdaptivePoolBuilderConfigurator<T> where T : notnull
    {
        public Action<AdaptivePoolBuilder<T>> Configure { get; set; } = static _ => { };
    }
}

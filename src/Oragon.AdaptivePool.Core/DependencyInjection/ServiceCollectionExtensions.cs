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

        // CR-03 fix: capture the IHostApplicationLifetime ServiceType (if registered) at
        // registration time by scanning IServiceCollection. This avoids a hard reference to
        // Microsoft.Extensions.Hosting.Abstractions while still being deterministic — the old
        // implementation used sp.GetServices<object>() which returns nothing in standard DI
        // and compared the concrete class name "ApplicationLifetime" against the interface
        // name "IHostApplicationLifetime", so the probe always returned CancellationToken.None.
        var hostLifetimeServiceType = services
            .FirstOrDefault(s => s.ServiceType?.FullName == "Microsoft.Extensions.Hosting.IHostApplicationLifetime")
            ?.ServiceType;

        services.TryAddKeyedSingleton<IAdaptivePool<T>>(name, (sp, key) =>
        {
            var keyName = (string)key!;
            var configurator = sp.GetRequiredService<IOptionsMonitor<AdaptivePoolBuilderConfigurator<T>>>().Get(keyName);
            // Optional: pull application-stopping CT if the host registers it (no hard dep).
            var lifetimeCt = TryGetHostApplicationStoppingToken(sp, hostLifetimeServiceType);
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

    // Resolves IHostApplicationLifetime.ApplicationStopping via reflection without a hard
    // reference to Microsoft.Extensions.Hosting.Abstractions. The interface ServiceType
    // is captured at AddAdaptivePool time (when we still have IServiceCollection); here
    // we just resolve it from IServiceProvider and read its ApplicationStopping property.
    private static CancellationToken TryGetHostApplicationStoppingToken(IServiceProvider sp, Type? hostLifetimeServiceType)
    {
        if (hostLifetimeServiceType is null) return CancellationToken.None;
        var lifetime = sp.GetService(hostLifetimeServiceType);
        if (lifetime is null) return CancellationToken.None;
        var prop = lifetime.GetType().GetProperty(
            "ApplicationStopping",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (prop?.GetValue(lifetime) is CancellationToken ct) return ct;
        return CancellationToken.None;
    }

    private sealed class AdaptivePoolBuilderConfigurator<T> where T : notnull
    {
        public Action<AdaptivePoolBuilder<T>> Configure { get; set; } = static _ => { };
    }
}

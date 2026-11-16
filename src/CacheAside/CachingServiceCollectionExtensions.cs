using CacheAside.Internal;
using CacheAside.Internal.Caches;
using CacheAside.Internal.Proxying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CacheAside;

/// <summary>Registration for <see cref="CacheableAttribute"/>, <see cref="CacheEvictAttribute"/> and <see cref="CacheKeyFromAttribute{TService}"/>.</summary>
public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Turns on the caching attributes for every service registered so far. Call it after registering the services
    /// it should cache. Only interface registrations with an implementation type (<c>AddScoped&lt;IFoo, Foo&gt;()</c>)
    /// are wrapped.
    /// </summary>
    /// <remarks>
    /// Everything is checked here, so a mistake fails at startup instead of on the first call: unknown key arguments,
    /// unsupported return types, unknown cache names, and attributes that could never run (on a controller, a factory
    /// registration, a method that isn't on the interface, ...). In a host, startup also fails if an annotated service
    /// is registered after this call. If anything is wrong, <paramref name="services"/> is left unchanged.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Sets up the default cache and any named caches.</param>
    public static IServiceCollection AddAnnotationCaching(this IServiceCollection services, Action<CachingOptions>? configureOptions = null)
    {
        if (services.Any(d => d.ServiceType == typeof(CacheRegistry))) throw CachingErrors.AddedTwice();

        var options = new CachingOptions();
        configureOptions?.Invoke(options);
        var catalog = new CacheCatalog(options);

        // Phase 1: build and validate everything without touching the collection.
        var registrations = CachingProxyRegistrar.FindAnnotatedServices(services, catalog);
        UnreachableAnnotations.ThrowIfAny(services, registrations);

        if (catalog.DefaultUsed && !catalog.DefaultConfigured && !services.Any(d => d.ServiceType == typeof(ICacheService)))
            throw CachingErrors.NoDefaultCache();

        // Phase 2: apply.
        services.AddSingleton(sp => new CacheRegistry(sp, catalog));
        CachingProxyRegistrar.Apply(services, registrations);

        // Fails host startup if an annotated service is registered after this call.
        var lateCheck = new LateRegistrationCheck(services);
        services.AddSingleton<IHostedService>(lateCheck);
        lateCheck.MarkKnown();

        return services;
    }
}

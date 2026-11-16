using System.Collections.Frozen;
using System.Reflection;
using CacheAside.Internal.Caches;
using CacheAside.Internal.Plans;
using Castle.DynamicProxy;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Internal.Proxying;

/// <summary>A service registration that has caching annotations and will be replaced by a proxy.</summary>
internal sealed record ProxyRegistration(
    int Index,
    ServiceDescriptor Descriptor,
    Type ImplementationType,
    FrozenDictionary<MethodInfo, CachePlan> Plans);

/// <summary>Finds annotated service registrations and swaps them for caching proxies.</summary>
internal static class CachingProxyRegistrar
{
    // One generator per process: Castle caches generated proxy types per generator.
    private static readonly ProxyGenerator ProxyGenerator = new();

    /// <summary>
    /// Builds the plans for every proxyable registration that has annotations. Only interface
    /// registrations with an implementation type qualify (not factories, instances, keyed services or
    /// open generics). Does not modify <paramref name="services"/>.
    /// </summary>
    public static List<ProxyRegistration> FindAnnotatedServices(IServiceCollection services, CacheCatalog catalog)
    {
        var found = new List<ProxyRegistration>();
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (!IsProxyable(descriptor)) continue;

            var plans = CachePlanBuilder.Build(descriptor.ServiceType, descriptor.ImplementationType!, catalog);
            if (plans.Count == 0) continue;

            ValidateKeySources(services, descriptor, plans);
            found.Add(new ProxyRegistration(i, descriptor, descriptor.ImplementationType!, plans));
        }
        return found;
    }

    /// <summary>
    /// Every [CacheKeyFrom&lt;T&gt;] service must be registered, and a singleton can't read a scoped or transient
    /// one: it is created once, so it would keep the first instance (e.g. the first user) forever.
    /// </summary>
    private static void ValidateKeySources(IServiceCollection services, ServiceDescriptor descriptor, FrozenDictionary<MethodInfo, CachePlan> plans)
    {
        foreach (var (method, plan) in plans)
        {
            var name = $"{method.DeclaringType}.{method.Name}";
            foreach (var source in plan.Key.ServiceTypes)
            {
                var registration = services.LastOrDefault(d => !d.IsKeyedService &&
                    (d.ServiceType == source || (source.IsGenericType && d.ServiceType == source.GetGenericTypeDefinition())))
                    ?? throw CachingErrors.KeySourceNotRegistered(name, source);

                if (descriptor.Lifetime == ServiceLifetime.Singleton && registration.Lifetime != ServiceLifetime.Singleton)
                    throw CachingErrors.KeySourceLifetime(name, descriptor.ServiceType, source, registration.Lifetime.ToString().ToLowerInvariant());
            }
        }
    }

    /// <summary>A non-keyed, closed interface registration with an implementation type.</summary>
    public static bool IsProxyable(ServiceDescriptor descriptor) =>
        !descriptor.IsKeyedService &&
        descriptor.ServiceType.IsInterface &&
        !descriptor.ServiceType.IsGenericTypeDefinition &&
        descriptor.ImplementationType is not null;

    /// <summary>
    /// Replaces each registration in place (preserving order, so "last registration wins" and
    /// IEnumerable&lt;T&gt; order are unchanged) and registers the implementation type itself so the
    /// proxy can resolve its target.
    /// </summary>
    public static void Apply(IServiceCollection services, IEnumerable<ProxyRegistration> registrations)
    {
        foreach (var (index, descriptor, implementationType, plans) in registrations)
        {
            services[index] = CreateProxyDescriptor(descriptor.ServiceType, implementationType, descriptor.Lifetime, plans);

            if (!services.Any(d => !d.IsKeyedService && d.ServiceType == implementationType))
            {
                services.Add(new ServiceDescriptor(implementationType, implementationType, descriptor.Lifetime));
            }
        }
    }

    private static ServiceDescriptor CreateProxyDescriptor(
        Type serviceType,
        Type implementationType,
        ServiceLifetime lifetime,
        FrozenDictionary<MethodInfo, CachePlan> plans)
    {
        var proxyOptions = new ProxyGenerationOptions(new CachingProxyGenerationHook(serviceType, implementationType, plans));

        return new ServiceDescriptor(serviceType, sp =>
        {
            var target = sp.GetRequiredService(implementationType);
            var interceptor = new CachingInterceptor(sp.GetRequiredService<CacheRegistry>(), plans, sp);
            return ProxyGenerator.CreateInterfaceProxyWithTarget(serviceType, target, proxyOptions, interceptor);
        }, lifetime);
    }
}

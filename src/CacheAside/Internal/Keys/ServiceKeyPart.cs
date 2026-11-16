using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Internal.Keys;

/// <summary>
/// A key segment read from an injected service ([CacheKeyFrom&lt;T&gt;]): the service is resolved on each
/// call and projected through a compiled, null-safe member path. A null value fails the call.
/// </summary>
internal sealed class ServiceKeyPart(Type serviceType, Func<object?, object?> accessor, string method, string description)
{
    public Type ServiceType { get; } = serviceType;

    public object GetValue(IServiceProvider services) =>
        accessor(services.GetRequiredService(ServiceType)) ?? throw CachingErrors.NullKeySource(method, description);
}

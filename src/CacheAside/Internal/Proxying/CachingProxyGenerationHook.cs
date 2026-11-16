using System.Collections.Frozen;
using System.Reflection;
using CacheAside.Internal.Plans;
using Castle.DynamicProxy;

namespace CacheAside.Internal.Proxying;

/// <summary>
/// Tells Castle to intercept only annotated methods; every other member calls the target directly.
/// Equality is by (service, implementation) so Castle reuses the generated proxy type.
/// </summary>
internal sealed class CachingProxyGenerationHook(
    Type serviceType,
    Type implementationType,
    FrozenDictionary<MethodInfo, CachePlan> plans) : IProxyGenerationHook
{
    private Type ServiceType { get; } = serviceType;
    private Type ImplementationType { get; } = implementationType;

    public void MethodsInspected() { }

    public void NonProxyableMemberNotification(Type type, MemberInfo memberInfo) { }

    public bool ShouldInterceptMethod(Type type, MethodInfo methodInfo) => plans.ContainsKey(methodInfo);

    public override bool Equals(object? obj) =>
        obj is CachingProxyGenerationHook other &&
        other.ServiceType == ServiceType &&
        other.ImplementationType == ImplementationType;

    public override int GetHashCode() => HashCode.Combine(ServiceType, ImplementationType);
}

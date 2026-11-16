using System.Collections.Frozen;
using System.Reflection;
using CacheAside.Internal.Caches;
using CacheAside.Internal.Plans;
using Castle.DynamicProxy;

namespace CacheAside.Internal.Proxying;

/// <summary>
/// Dispatches each intercepted call to the plan precomputed for that method, with that plan's cache.
/// Only annotated methods are routed here (see <see cref="CachingProxyGenerationHook"/>).
/// <paramref name="services"/> are the services this proxy was created with (the request scope for a
/// scoped service), used to resolve [CacheKeyFrom&lt;T&gt;] values.
/// </summary>
internal sealed class CachingInterceptor(
    CacheRegistry caches,
    FrozenDictionary<MethodInfo, CachePlan> plans,
    IServiceProvider services) : IInterceptor
{
    public void Intercept(IInvocation invocation)
    {
        if (plans.TryGetValue(invocation.Method, out var plan))
        {
            plan.Intercept(invocation, caches.Get(plan.CacheSlot), services);
        }
        else
        {
            invocation.Proceed();
        }
    }
}

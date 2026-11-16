using CacheAside.Internal.Keys;
using Castle.DynamicProxy;

namespace CacheAside.Internal.Plans;

/// <summary>
/// Everything needed to handle one annotated method, built once at startup by <see cref="CachePlanBuilder"/>,
/// so that a call only has to build its key and talk to the cache.
/// </summary>
internal abstract class CachePlan(int cacheSlot, CacheKeyTemplate key)
{
    /// <summary>Slot of this method's cache in the <see cref="Caches.CacheRegistry"/> (0 = default cache).</summary>
    public int CacheSlot { get; } = cacheSlot;

    public CacheKeyTemplate Key { get; } = key;

    /// <summary>Handles one call. <c>services</c> are the services the proxy was created with, for [CacheKeyFrom&lt;T&gt;].</summary>
    public abstract void Intercept(IInvocation invocation, ICacheService cache, IServiceProvider services);
}

using CacheAside.Internal.Keys;
using Castle.DynamicProxy;

namespace CacheAside.Internal.Plans;

/// <summary>[Cacheable] on a method returning <see cref="Task{T}"/>.</summary>
internal sealed class CacheablePlan<T>(
    int cacheSlot,
    CacheKeyTemplate key,
    TimeSpan? duration,
    Func<object, object?[], Task<T>> invoker) : CachePlan(cacheSlot, key)
{
    public override void Intercept(IInvocation invocation, ICacheService cache, IServiceProvider services)
    {
        // The real method runs only on a miss, called directly through a compiled delegate,
        // so the invocation is never touched after this method returns.
        var target = invocation.InvocationTarget!; // always set for interface proxies with a target
        var args = invocation.Arguments;

        string key;
        try
        {
            key = Key.Build(args, services);
        }
        catch (Exception ex)
        {
            // Fail like an async method would: through the returned task, without running the method.
            invocation.ReturnValue = Task.FromException<T>(ex);
            return;
        }

        invocation.ReturnValue = cache.GetOrCreateAsync(key, () => invoker(target, args), duration);
    }
}

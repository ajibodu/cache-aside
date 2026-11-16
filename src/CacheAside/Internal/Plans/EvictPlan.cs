using CacheAside.Internal.Keys;
using Castle.DynamicProxy;

namespace CacheAside.Internal.Plans;

/// <summary>[CacheEvict] on a method returning <see cref="Task"/>: removes the key once the method succeeds.</summary>
internal sealed class EvictPlan(int cacheSlot, CacheKeyTemplate key) : CachePlan(cacheSlot, key)
{
    public override void Intercept(IInvocation invocation, ICacheService cache, IServiceProvider services)
    {
        // Built from the arguments as passed, before the method runs. If it can't be built, the method
        // doesn't run either: running it without being able to evict would leave a stale entry behind.
        string key;
        try
        {
            key = Key.Build(invocation.Arguments, services);
        }
        catch (Exception ex)
        {
            invocation.ReturnValue = Task.FromException(ex);
            return;
        }
        invocation.Proceed();
        invocation.ReturnValue = EvictAfterAsync((Task)invocation.ReturnValue!, key, cache);
    }

    private static async Task EvictAfterAsync(Task task, string key, ICacheService cache)
    {
        await task.ConfigureAwait(false);
        await cache.RemoveAsync(key).ConfigureAwait(false);
    }
}

/// <summary>[CacheEvict] on a method returning <see cref="Task{T}"/>: removes the key once the method succeeds and passes its result through.</summary>
internal sealed class EvictPlan<T>(int cacheSlot, CacheKeyTemplate key) : CachePlan(cacheSlot, key)
{
    public override void Intercept(IInvocation invocation, ICacheService cache, IServiceProvider services)
    {
        string key;
        try
        {
            key = Key.Build(invocation.Arguments, services);
        }
        catch (Exception ex)
        {
            invocation.ReturnValue = Task.FromException<T>(ex);
            return;
        }
        invocation.Proceed();
        invocation.ReturnValue = EvictAfterAsync((Task<T>)invocation.ReturnValue!, key, cache);
    }

    private static async Task<T> EvictAfterAsync(Task<T> task, string key, ICacheService cache)
    {
        var result = await task.ConfigureAwait(false);
        await cache.RemoveAsync(key).ConfigureAwait(false);
        return result;
    }
}

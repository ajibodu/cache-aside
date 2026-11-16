using System.Collections.Frozen;
using System.Reflection;
using CacheAside.Internal.Caches;
using CacheAside.Internal.Keys;

namespace CacheAside.Internal.Plans;

/// <summary>
/// Turns the annotated methods of a service into <see cref="CachePlan"/>s at startup. All reflection,
/// validation and compilation happens here; misconfiguration throws here rather than on the first call.
/// </summary>
internal static class CachePlanBuilder
{
    private static readonly MethodInfo CreateCacheablePlanMethod = GetFactory(nameof(CreateCacheablePlan));
    private static readonly MethodInfo CreateEvictPlanMethod = GetFactory(nameof(CreateEvictPlan));

    /// <summary>
    /// Builds a plan for every annotated method of <paramref name="serviceType"/> (including inherited
    /// interfaces), keyed by the interface method. Attributes may sit on the interface method or the
    /// implementation method; the implementation's wins if both have one.
    /// </summary>
    public static FrozenDictionary<MethodInfo, CachePlan> Build(Type serviceType, Type implementationType, CacheCatalog catalog)
    {
        var plans = new Dictionary<MethodInfo, CachePlan>();

        foreach (var iface in serviceType.GetInterfaces().Prepend(serviceType))
        {
            var map = implementationType.GetInterfaceMap(iface);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (TryCreate(map.InterfaceMethods[i], map.TargetMethods[i], catalog) is { } plan)
                {
                    plans[map.InterfaceMethods[i]] = plan;
                }
            }
        }

        return plans.ToFrozenDictionary();
    }

    private static CachePlan? TryCreate(MethodInfo interfaceMethod, MethodInfo targetMethod, CacheCatalog catalog)
    {
        var cacheable = FindAnnotation<CacheableAttribute>(interfaceMethod, targetMethod);
        var evict = FindAnnotation<CacheEvictAttribute>(interfaceMethod, targetMethod);
        var method = $"{interfaceMethod.DeclaringType}.{interfaceMethod.Name}";

        if (cacheable == null && evict == null)
        {
            if (KeySources(interfaceMethod).Any() || KeySources(targetMethod).Any())
                throw CachingErrors.KeySourceWithoutAnnotation(method);
            return null;
        }

        if (cacheable != null && evict != null)
            throw CachingErrors.InvalidAnnotation(method, "cannot have both [Cacheable] and [CacheEvict].");
        if (interfaceMethod.IsGenericMethodDefinition)
            throw CachingErrors.InvalidAnnotation(method, "generic methods are not supported.");
        if (interfaceMethod.GetParameters().Any(p => p.ParameterType.IsByRef))
            throw CachingErrors.InvalidAnnotation(method, "ref/out/in parameters are not supported.");

        var declaredOn = cacheable?.DeclaredOn ?? evict!.Value.DeclaredOn;
        var otherMethod = declaredOn == targetMethod ? interfaceMethod : targetMethod;
        if (otherMethod != declaredOn && KeySources(otherMethod).Any())
            throw CachingErrors.KeySourceOnOtherMethod(method);

        var returnType = interfaceMethod.ReturnType;
        var resultType = GetTaskResultType(returnType);

        if (cacheable is { } found)
        {
            var (cacheableAttribute, cacheableDeclaredOn) = found;
            if (resultType == null)
                throw CachingErrors.InvalidAnnotation(method, "[Cacheable] requires a Task<T> return type.");

            var (slot, key) = BuildKey(method, cacheableAttribute, cacheableDeclaredOn, catalog);
            TimeSpan? duration = cacheableAttribute.DurationSeconds > 0 ? TimeSpan.FromSeconds(cacheableAttribute.DurationSeconds) : null;
            return CreateGeneric(CreateCacheablePlanMethod, resultType, interfaceMethod, slot, key, duration);
        }

        var (evictAttribute, evictDeclaredOn) = evict!.Value;
        var (evictSlot, evictKey) = BuildKey(method, evictAttribute, evictDeclaredOn, catalog);

        if (returnType == typeof(Task)) return new EvictPlan(evictSlot, evictKey);
        if (resultType != null) return CreateGeneric(CreateEvictPlanMethod, resultType, evictSlot, evictKey);

        throw CachingErrors.InvalidAnnotation(method, "[CacheEvict] requires a Task or Task<T> return type.");
    }

    /// <summary>Resolves the key arguments and the cache (slot + prefix) for one annotation.</summary>
    private static (int Slot, CacheKeyTemplate Key) BuildKey(string method, ICacheAnnotation annotation, MethodInfo declaredOn, CacheCatalog catalog)
    {
        var parts = KeyPartResolver.Resolve(method, declaredOn, annotation.KeyArguments);
        var serviceParts = KeyPartResolver.ResolveServices(method, KeySources(declaredOn));
        var (slot, cachePrefix) = catalog.Resolve(annotation.Cache, method);
        return (slot, new CacheKeyTemplate(cachePrefix + annotation.KeyPrefix, serviceParts, parts));
    }

    /// <summary>The [CacheKeyFrom&lt;T&gt;] attributes on one method.</summary>
    private static IEnumerable<ICacheKeySource> KeySources(MethodInfo method) =>
        method.GetCustomAttributes(inherit: false).OfType<ICacheKeySource>();

    /// <summary>
    /// Finds the attribute on the implementation method first, then the interface method. Also returns
    /// the method it was found on, whose parameter names the key arguments refer to.
    /// </summary>
    private static (TAttribute Attribute, MethodInfo DeclaredOn)? FindAnnotation<TAttribute>(MethodInfo interfaceMethod, MethodInfo targetMethod)
        where TAttribute : Attribute
    {
        if (targetMethod.GetCustomAttribute<TAttribute>() is { } onTarget) return (onTarget, targetMethod);
        if (interfaceMethod.GetCustomAttribute<TAttribute>() is { } onInterface) return (onInterface, interfaceMethod);
        return null;
    }

    private static Type? GetTaskResultType(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : null;

    // Plans are generic over the method's result type T; these factories are closed over T by reflection, once per method.
    private static CachePlan CreateCacheablePlan<T>(MethodInfo method, int slot, CacheKeyTemplate key, TimeSpan? duration) =>
        new CacheablePlan<T>(slot, key, duration, TargetInvoker.Compile<T>(method));

    private static CachePlan CreateEvictPlan<T>(int slot, CacheKeyTemplate key) =>
        new EvictPlan<T>(slot, key);

    private static CachePlan CreateGeneric(MethodInfo factory, Type resultType, params object?[] args) =>
        (CachePlan)factory.MakeGenericMethod(resultType).Invoke(null, args)!;

    private static MethodInfo GetFactory(string name) =>
        typeof(CachePlanBuilder).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
}

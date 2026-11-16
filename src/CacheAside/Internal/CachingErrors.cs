namespace CacheAside.Internal;

/// <summary>Every configuration error the library raises. All of them are thrown at startup.</summary>
internal static class CachingErrors
{
    public static InvalidOperationException InvalidAnnotation(string method, string reason) =>
        new($"Invalid caching annotation on {method}: {reason}");

    public static InvalidOperationException UnknownCache(string method, string cacheName, IReadOnlyCollection<string> registered)
    {
        var known = registered.Count == 0 ? "none" : string.Join(", ", registered.Select(k => $"'{k}'"));
        return InvalidAnnotation(method,
            $"cache '{cacheName}' is not registered (registered caches: {known}). " +
            $"Add it with {nameof(CachingOptions)}.{nameof(CachingOptions.AddCache)}(\"{cacheName}\", ...).");
    }

    public static InvalidOperationException DuplicateCache(string name) =>
        new($"A cache named '{name}' has already been added.");

    public static InvalidOperationException CacheWithoutBackend(string name) =>
        new($"Cache '{name}' has no backend. Configure it with {nameof(CacheOptions.UseCache)}(...) or {nameof(CacheOptions.UseCacheService)}<T>().");

    public static InvalidOperationException NoDefaultCache() =>
        new("Some [Cacheable]/[CacheEvict] methods use the default cache (no Cache = \"name\"), but no default cache is configured. " +
            $"Configure it with {nameof(CachingOptions)}.{nameof(CacheOptions.UseCache)}(...) / {nameof(CacheOptions.UseCacheService)}<T>(), " +
            $"or register an {nameof(ICacheService)} before calling {nameof(CachingServiceCollectionExtensions.AddAnnotationCaching)}.");

    public static InvalidOperationException UnreachableAnnotations(IEnumerable<string> problems) =>
        new("These [Cacheable]/[CacheEvict] annotations would never run, because calls to them can't go through a caching proxy:" +
            string.Concat(problems.Select(p => $"{Environment.NewLine} - {p}")));

    public static InvalidOperationException KeySourceWithoutAnnotation(string method) =>
        InvalidAnnotation(method, "[CacheKeyFrom<T>] only works together with [Cacheable] or [CacheEvict] on the same method.");

    public static InvalidOperationException KeySourceOnOtherMethod(string method) =>
        InvalidAnnotation(method, "[CacheKeyFrom<T>] must be on the same method as its [Cacheable]/[CacheEvict] " +
                                  "(both on the interface method, or both on the implementation method).");

    public static InvalidOperationException KeySourceNotRegistered(string method, Type source) =>
        InvalidAnnotation(method, $"[CacheKeyFrom<{source.Name}>] reads {source.FullName}, which is not registered. " +
                                  $"Register it before calling {nameof(CachingServiceCollectionExtensions.AddAnnotationCaching)}.");

    public static InvalidOperationException KeySourceLifetime(string method, Type service, Type source, string sourceLifetime) =>
        InvalidAnnotation(method, $"{service.Name} is registered as a singleton, so it can't use [CacheKeyFrom<{source.Name}>]: " +
                                  $"{source.Name} is {sourceLifetime}, and a singleton would only ever see the first instance " +
                                  $"(e.g. the first user). Register {service.Name} as scoped or transient.");

    /// <summary>Thrown when a call is made (not at startup): a [CacheKeyFrom&lt;T&gt;] value was null.</summary>
    public static InvalidOperationException NullKeySource(string method, string source) =>
        new($"Cannot build the cache key for {method}: {source} is null. The call fails rather than sharing " +
            "one cache entry between every caller without a value.");

    public static InvalidOperationException AddedTwice() =>
        new($"{nameof(CachingServiceCollectionExtensions.AddAnnotationCaching)} can only be called once.");
}

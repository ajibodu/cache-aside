using CacheAside.Internal;

namespace CacheAside;

/// <summary>
/// Options for <see cref="CachingServiceCollectionExtensions.AddAnnotationCaching"/>.
/// The <c>Use...</c> methods called on this object configure the default cache (if none is configured,
/// the <see cref="ICacheService"/> registered in DI is used); <see cref="AddCache"/> adds named caches.
/// </summary>
public sealed class CachingOptions : CacheOptions
{
    internal Dictionary<string, CacheOptions> NamedCaches { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a named cache. Select it with <c>Cache = "name"</c> on [Cacheable] / [CacheEvict].
    /// </summary>
    public CachingOptions AddCache(string name, Action<CacheOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (NamedCaches.ContainsKey(name)) throw CachingErrors.DuplicateCache(name);

        var cache = new CacheOptions();
        configure(cache);
        if (!cache.IsConfigured) throw CachingErrors.CacheWithoutBackend(name);

        NamedCaches.Add(name, cache);
        return this;
    }
}

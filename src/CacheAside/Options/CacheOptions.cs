using CacheAside.Internal.Caches;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside;

/// <summary>
/// Configures one cache: the default cache (on <see cref="CachingOptions"/> itself) or a named one
/// added with <see cref="CachingOptions.AddCache"/>. The library knows nothing about cache technology:
/// you plug in your cache with one of the <c>Use...</c> methods.
/// </summary>
public class CacheOptions
{
    /// <summary>Set through the <c>keyPrefix</c> argument of the <c>Use...</c> methods.</summary>
    internal string? KeyPrefix { get; private set; }

    internal Func<IServiceProvider, CacheInstance>? Factory { get; private set; }

    internal bool IsConfigured => Factory != null;

    /// <summary>
    /// Maps three byte-level operations onto your cache. The library runs the read/miss/write flow and
    /// serializes values to UTF-8 JSON; there is no stampede protection (concurrent misses each run the
    /// method). For a cache that coordinates misses (e.g. HybridCache), use the getOrCreate overload or
    /// <see cref="UseCacheService{TService}"/>.
    /// <typeparamref name="TCache"/> is resolved from DI on first use.
    /// </summary>
    /// <param name="get">Return the stored bytes, or null if the key is absent.</param>
    /// <param name="set">Store the bytes; ttl is null when the entry should not expire.</param>
    /// <param name="remove">Delete the key (no-op if absent).</param>
    /// <param name="keyPrefix">
    /// Optional. Prepended to every key stored in this cache, in front of the attribute's prefix:
    /// "myapp:" + [Cacheable("account", ...)] gives "myapp:account:ACC-1".
    /// </param>
    public CacheOptions UseCache<TCache>(
        Func<TCache, string, Task<byte[]?>> get,
        Func<TCache, string, byte[], TimeSpan?, Task> set,
        Func<TCache, string, Task> remove,
        string? keyPrefix = null) where TCache : class
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);
        return Configure(
            sp => new CacheInstance(new GetSetRemoveCacheAdapter<TCache>(sp.GetRequiredService<TCache>(), get, set, remove)),
            keyPrefix);
    }

    /// <summary>
    /// Alternative to get/set/remove: hands the whole read/miss/write flow to your cache, so its own
    /// guarantees apply (e.g. HybridCache's stampede protection: concurrent misses run the method once).
    /// The library only converts values to and from UTF-8 JSON bytes; a null result is stored as JSON null.
    /// <typeparamref name="TCache"/> is resolved from DI on first use.
    /// </summary>
    /// <param name="getOrCreate">
    /// Return the bytes stored under the key; on a miss, call the supplied factory, store its bytes
    /// with the ttl (null = no expiry) and return them.
    /// </param>
    /// <param name="remove">Delete the key (no-op if absent).</param>
    /// <param name="keyPrefix">
    /// Optional. Prepended to every key stored in this cache, in front of the attribute's prefix.
    /// </param>
    public CacheOptions UseCache<TCache>(
        Func<TCache, string, Func<Task<byte[]>>, TimeSpan?, Task<byte[]>> getOrCreate,
        Func<TCache, string, Task> remove,
        string? keyPrefix = null) where TCache : class
    {
        ArgumentNullException.ThrowIfNull(getOrCreate);
        ArgumentNullException.ThrowIfNull(remove);
        return Configure(
            sp => new CacheInstance(new GetOrCreateCacheAdapter<TCache>(sp.GetRequiredService<TCache>(), getOrCreate, remove)),
            keyPrefix);
    }

    /// <summary>
    /// Uses your own <see cref="ICacheService"/> implementation (typed values, no JSON): the instance
    /// registered in DI if there is one, otherwise one created with constructor injection and
    /// disposed by the library with the container.
    /// </summary>
    /// <param name="keyPrefix">
    /// Optional. Prepended to every key stored in this cache, in front of the attribute's prefix.
    /// </param>
    public CacheOptions UseCacheService<TService>(string? keyPrefix = null) where TService : class, ICacheService =>
        Configure(sp =>
        {
            if (sp.GetService<TService>() is { } registered) return new CacheInstance(registered);
            var created = ActivatorUtilities.CreateInstance<TService>(sp);
            return new CacheInstance(created, created as IDisposable);
        }, keyPrefix);

    private CacheOptions Configure(Func<IServiceProvider, CacheInstance> factory, string? keyPrefix)
    {
        Factory = factory;
        KeyPrefix = keyPrefix;
        return this;
    }
}

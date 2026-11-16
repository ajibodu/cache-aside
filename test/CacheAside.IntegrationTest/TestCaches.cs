using CacheAside;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>
/// Cache backends for the tests. The Cacheable library ships none of these: this is exactly the
/// kind of mapping an app writes once in its own code.
/// </summary>
public static class TestCaches
{
    /// <summary>Typed in-memory backend over the app's IMemoryCache (needs services.AddMemoryCache()).</summary>
    public static CacheOptions UseMemory(this CacheOptions c, string? keyPrefix = null) => c.UseCacheService<MemoryCacheService>(keyPrefix);

    /// <summary>In-memory backend with its own private MemoryCache, so two of them never share keys.</summary>
    public static CacheOptions UseIsolatedMemory(this CacheOptions c, string? keyPrefix = null) => c.UseCacheService<IsolatedMemoryCacheService>(keyPrefix);

    /// <summary>Redis via the app's IConnectionMultiplexer (get/set/remove: no stampede protection).</summary>
    public static CacheOptions UseRedis(this CacheOptions c, string? keyPrefix = null) => c.UseCache<IConnectionMultiplexer>(
        get: async (redis, key) => (byte[]?)await redis.GetDatabase().StringGetAsync(key),
        set: (redis, key, value, ttl) => redis.GetDatabase().StringSetAsync(key, value, ttl, When.Always, CommandFlags.None),
        remove: (redis, key) => redis.GetDatabase().KeyDeleteAsync(key),
        keyPrefix: keyPrefix);

    /// <summary>The app's IDistributedCache (get/set/remove: no stampede protection).</summary>
    public static CacheOptions UseDistributedCache(this CacheOptions c, string? keyPrefix = null) => c.UseCache<IDistributedCache>(
        get: (cache, key) => cache.GetAsync(key),
        set: (cache, key, value, ttl) => cache.SetAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }),
        remove: (cache, key) => cache.RemoveAsync(key),
        keyPrefix: keyPrefix);

    /// <summary>HybridCache as a typed ICacheService: HybridCache owns the miss flow, including stampede protection.</summary>
    public static CacheOptions UseHybridCache(this CacheOptions c, string? keyPrefix = null) => c.UseCacheService<HybridCacheService>(keyPrefix);

    /// <summary>HybridCache via the getOrCreate delegate (bytes): same stampede protection, no class needed.</summary>
    public static CacheOptions UseHybridCacheBytes(this CacheOptions c, string? keyPrefix = null) => c.UseCache<HybridCache>(
        getOrCreate: (cache, key, factory, ttl) => cache.GetOrCreateAsync(
            key,
            factory,
            static async (f, _) => await f(),
            new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl }).AsTask(),
        remove: (cache, key) => cache.RemoveAsync(key).AsTask(),
        keyPrefix: keyPrefix);
}

/// <summary>
/// Typed ICacheService over HybridCache (needs services.AddHybridCache()). HybridCache runs the whole
/// read/miss/write flow, so concurrent misses for a key run the method once; with an IDistributedCache
/// registered (e.g. AddStackExchangeRedisCache) it also uses it as L2.
/// </summary>
public sealed class HybridCacheService(HybridCache cache) : ICacheService
{
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null) =>
        cache.GetOrCreateAsync(
            key,
            factory,
            static async (f, _) => await f(),
            new HybridCacheEntryOptions { Expiration = duration, LocalCacheExpiration = duration }).AsTask();

    public Task RemoveAsync(string key) => cache.RemoveAsync(key).AsTask();
}

/// <summary>Typed ICacheService over IMemoryCache: stores objects as-is, honours TTL.</summary>
public class MemoryCacheService(IMemoryCache memoryCache) : ICacheService
{
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null)
    {
        if (memoryCache.TryGetValue(key, out var cached)) return Task.FromResult((T)cached!);
        return CreateAsync(key, factory, duration);
    }

    private async Task<T> CreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration)
    {
        var value = await factory();
        using var entry = memoryCache.CreateEntry(key);
        entry.Value = value;
        if (duration.HasValue) entry.AbsoluteExpirationRelativeToNow = duration;
        return value;
    }

    public Task RemoveAsync(string key)
    {
        memoryCache.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class IsolatedMemoryCacheService : MemoryCacheService, IDisposable
{
    private readonly MemoryCache _cache;

    public IsolatedMemoryCacheService() : this(new MemoryCache(new MemoryCacheOptions())) { }
    private IsolatedMemoryCacheService(MemoryCache cache) : base(cache) => _cache = cache;

    public void Dispose() => _cache.Dispose();
}

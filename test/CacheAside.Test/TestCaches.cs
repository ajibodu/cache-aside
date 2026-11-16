using CacheAside;
using Microsoft.Extensions.Caching.Memory;

namespace CacheAside.Test;

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

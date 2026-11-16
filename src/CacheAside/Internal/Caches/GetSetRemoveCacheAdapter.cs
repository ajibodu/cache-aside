using System.Text.Json;

namespace CacheAside.Internal.Caches;

/// <summary>
/// <c>UseCache(get, set, remove)</c>: the library runs the read/miss/write flow over three byte-level
/// delegates, serializing values to UTF-8 JSON. No stampede protection (concurrent misses each run the
/// method); null results are not stored.
/// </summary>
internal sealed class GetSetRemoveCacheAdapter<TCache>(
    TCache cache,
    Func<TCache, string, Task<byte[]?>> get,
    Func<TCache, string, byte[], TimeSpan?, Task> set,
    Func<TCache, string, Task> remove) : ICacheService
{
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null)
    {
        var cached = await get(cache, key).ConfigureAwait(false);
        if (cached != null)
        {
            return JsonSerializer.Deserialize<T>(cached)!;
        }

        var value = await factory().ConfigureAwait(false);
        if (value != null)
        {
            await set(cache, key, JsonSerializer.SerializeToUtf8Bytes(value), duration).ConfigureAwait(false);
        }
        return value;
    }

    public Task RemoveAsync(string key) => remove(cache, key);
}

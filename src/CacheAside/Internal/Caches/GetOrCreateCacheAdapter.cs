using System.Text.Json;

namespace CacheAside.Internal.Caches;

/// <summary>
/// <c>UseCache(getOrCreate, remove)</c>: the whole read/miss/write flow belongs to the caller's cache,
/// so its guarantees (stampede protection, L1/L2, ...) apply. The library only converts values to and
/// from UTF-8 JSON bytes. A null result is stored as JSON <c>null</c>.
/// </summary>
internal sealed class GetOrCreateCacheAdapter<TCache>(
    TCache cache,
    Func<TCache, string, Func<Task<byte[]>>, TimeSpan?, Task<byte[]>> getOrCreate,
    Func<TCache, string, Task> remove) : ICacheService
{
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null)
    {
        var bytes = await getOrCreate(cache, key, async () =>
        {
            var value = await factory().ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(value);
        }, duration).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(bytes)!;
    }

    public Task RemoveAsync(string key) => remove(cache, key);
}

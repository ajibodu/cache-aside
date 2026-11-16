namespace CacheAside.Internal.Caches;

/// <summary>
/// Holds the cache instance for each <see cref="CacheCatalog"/> slot, one registry per container.
/// Each cache is created on first use; after that a lookup is a single array read.
/// Disposes whatever the library created (e.g. <c>UseCacheService</c> instances it constructed).
/// </summary>
internal sealed class CacheRegistry(IServiceProvider serviceProvider, CacheCatalog catalog) : IDisposable
{
    private readonly ICacheService?[] _caches = new ICacheService?[catalog.Factories.Length];
    private readonly List<IDisposable> _owned = [];
    private readonly object _lock = new();

    public ICacheService Get(int slot) => Volatile.Read(ref _caches[slot]) ?? Create(slot);

    private ICacheService Create(int slot)
    {
        lock (_lock)
        {
            if (_caches[slot] is { } existing) return existing;

            var instance = catalog.Factories[slot](serviceProvider);
            if (instance.Owned != null) _owned.Add(instance.Owned);
            Volatile.Write(ref _caches[slot], instance.Service);
            return instance.Service;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var owned in _owned) owned.Dispose();
            _owned.Clear();
        }
    }
}

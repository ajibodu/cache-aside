using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Internal.Caches;

/// <summary>
/// Startup-time view of the configured caches. Every cache gets a slot: 0 is the default cache,
/// named caches follow in the order they were added. Plans store a slot index and a ready-joined
/// prefix, so no cache name is ever looked up during a call.
/// </summary>
internal sealed class CacheCatalog
{
    public const int DefaultSlot = 0;

    private readonly Dictionary<string, int> _slotByName = new(StringComparer.Ordinal);
    private readonly string[] _prefixes;

    /// <summary>How to create the cache in each slot; used by <see cref="CacheRegistry"/>.</summary>
    public Func<IServiceProvider, CacheInstance>[] Factories { get; }

    /// <summary>True when the default cache was configured on the options (rather than taken from DI).</summary>
    public bool DefaultConfigured { get; }

    /// <summary>True once any annotated method has resolved to the default cache.</summary>
    public bool DefaultUsed { get; private set; }

    public CacheCatalog(CachingOptions options)
    {
        DefaultConfigured = options.IsConfigured;

        var factories = new List<Func<IServiceProvider, CacheInstance>>
        {
            // Default: the cache configured on the options, otherwise the ICacheService registered in DI.
            options.Factory ?? (sp => new CacheInstance(sp.GetRequiredService<ICacheService>())),
        };
        var prefixes = new List<string> { options.KeyPrefix ?? string.Empty };

        foreach (var (name, cache) in options.NamedCaches)
        {
            _slotByName[name] = factories.Count;
            factories.Add(cache.Factory!); // AddCache rejects caches without a backend
            prefixes.Add(cache.KeyPrefix ?? string.Empty);
        }

        Factories = factories.ToArray();
        _prefixes = prefixes.ToArray();
    }

    /// <summary>Maps an attribute's <c>Cache</c> name (null = default) to its slot and key prefix.</summary>
    public (int Slot, string Prefix) Resolve(string? cacheName, string method)
    {
        if (cacheName == null)
        {
            DefaultUsed = true;
            return (DefaultSlot, _prefixes[DefaultSlot]);
        }

        return _slotByName.TryGetValue(cacheName, out var slot)
            ? (slot, _prefixes[slot])
            : throw CachingErrors.UnknownCache(method, cacheName, _slotByName.Keys);
    }
}

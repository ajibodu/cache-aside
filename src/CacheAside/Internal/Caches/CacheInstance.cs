namespace CacheAside.Internal.Caches;

/// <summary>A resolved cache plus anything the library created for it and must dispose.</summary>
internal readonly record struct CacheInstance(ICacheService Service, IDisposable? Owned = null);

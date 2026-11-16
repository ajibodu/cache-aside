namespace CacheAside;

/// <summary>
/// A cache the attributes can use. Implement it when you want full control (typed values, your own stampede
/// handling), and plug it in with <see cref="CacheOptions.UseCacheService{TService}"/>. For most caches the
/// <c>UseCache</c> delegate overloads are simpler.
/// </summary>
public interface ICacheService
{
    /// <summary>Returns the cached value for <paramref name="key"/>, or runs <paramref name="factory"/> and caches its result.</summary>
    /// <param name="key">The full cache key.</param>
    /// <param name="factory">Calls the real method. Only run on a miss.</param>
    /// <param name="duration">How long to keep the value. <c>null</c> means it doesn't expire.</param>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null);

    /// <summary>Removes <paramref name="key"/>. Does nothing if it isn't cached.</summary>
    Task RemoveAsync(string key);
}

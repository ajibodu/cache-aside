using System.Collections.Concurrent;
using System.Text;
using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>Mapping ICacheService onto an arbitrary, app-specific cache with UseCache.</summary>
public class CustomCacheMappingTests
{
    /// <summary>
    /// Stands in for an in-house cache with its own API shape: string payloads, TTL in whole seconds,
    /// differently named methods. Nothing here knows about the library.
    /// </summary>
    public interface ICompanyCache
    {
        Task<string?> ReadAsync(string key);
        Task WriteAsync(string key, string payload, int ttlSeconds);
        Task DeleteAsync(string key);
    }

    public class CompanyCache : ICompanyCache
    {
        public ConcurrentDictionary<string, (string Payload, int TtlSeconds)> Store { get; } = new();
        public List<string> Deleted { get; } = [];

        public Task<string?> ReadAsync(string key) =>
            Task.FromResult(Store.TryGetValue(key, out var entry) ? entry.Payload : null);

        public Task WriteAsync(string key, string payload, int ttlSeconds)
        {
            Store[key] = (payload, ttlSeconds);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            Store.TryRemove(key, out _);
            Deleted.Add(key);
            return Task.CompletedTask;
        }
    }

    public record Rate(string Pair, decimal Value);

    public interface IRateProvider
    {
        [Cacheable("rate", 120, "from", "to")]
        Task<Rate> GetAsync(string from, string to);

        [CacheEvict("rate", "from", "to")]
        Task RefreshAsync(string from, string to);

        int Calls { get; }
    }

    public class RateProvider : IRateProvider
    {
        private int _calls;
        public int Calls => _calls;

        public Task<Rate> GetAsync(string from, string to)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new Rate($"{from}{to}", 1520.75m));
        }

        public Task RefreshAsync(string from, string to) => Task.CompletedTask;
    }

    private static void MapCompanyCache(CachingOptions o) => o.UseCache<ICompanyCache>(
        get: async (cache, key) => await cache.ReadAsync(key) is { } payload ? Encoding.UTF8.GetBytes(payload) : null,
        set: (cache, key, value, ttl) => cache.WriteAsync(key, Encoding.UTF8.GetString(value), (int)(ttl?.TotalSeconds ?? 0)),
        remove: (cache, key) => cache.DeleteAsync(key));

    [Fact]
    public async Task Calls_are_routed_through_the_mapped_custom_cache()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRateProvider, RateProvider>();
        services.AddAnnotationCaching(MapCompanyCache);
        services.AddSingleton<ICompanyCache, CompanyCache>(); // registered after: resolved lazily
        using var provider = services.BuildServiceProvider();
        var rates = provider.GetRequiredService<IRateProvider>();
        var companyCache = (CompanyCache)provider.GetRequiredService<ICompanyCache>();

        var first = await rates.GetAsync("USD", "NGN");
        var second = await rates.GetAsync("USD", "NGN");

        second.Should().Be(first);
        rates.Calls.Should().Be(1);

        companyCache.Store.Should().ContainKey("rate:USD:NGN");
        var (payload, ttlSeconds) = companyCache.Store["rate:USD:NGN"];
        payload.Should().Be("""{"Pair":"USDNGN","Value":1520.75}""");
        ttlSeconds.Should().Be(120, "durationSeconds is passed to the mapping as the ttl");
    }

    [Fact]
    public async Task Evict_is_routed_through_the_mapped_remove()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICompanyCache, CompanyCache>();
        services.AddSingleton<IRateProvider, RateProvider>();
        services.AddAnnotationCaching(MapCompanyCache);
        using var provider = services.BuildServiceProvider();
        var rates = provider.GetRequiredService<IRateProvider>();
        var companyCache = (CompanyCache)provider.GetRequiredService<ICompanyCache>();

        await rates.GetAsync("USD", "NGN");
        await rates.RefreshAsync("USD", "NGN");
        await rates.GetAsync("USD", "NGN");

        companyCache.Deleted.Should().Equal("rate:USD:NGN");
        rates.Calls.Should().Be(2);
    }

    [Fact]
    public void Using_the_default_cache_without_configuring_one_fails_at_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRateProvider, RateProvider>();

        var act = () => services.AddAnnotationCaching();

        act.Should().Throw<InvalidOperationException>().WithMessage("*no default cache is configured*UseCache*");
        services.Single(d => d.ServiceType == typeof(IRateProvider)).ImplementationType
            .Should().Be(typeof(RateProvider), "a failed AddAnnotationCaching leaves the collection untouched");
    }

    [Fact]
    public void No_cache_configuration_is_needed_when_nothing_uses_the_default_cache()
    {
        var act = () => new ServiceCollection().AddAnnotationCaching();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Default_cache_configured_on_the_options_wins_over_an_ICacheService_in_DI()
    {
        var registered = new RecordingCacheService();
        var configured = new RecordingCacheService();
        var services = new ServiceCollection();
        services.AddSingleton<ICacheService>(registered);
        services.AddSingleton(configured);
        services.AddSingleton<IRateProvider, RateProvider>();
        services.AddAnnotationCaching(o => o.UseCacheService<RecordingCacheService>());
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IRateProvider>().GetAsync("EUR", "NGN");

        configured.Keys.Should().Equal("rate:EUR:NGN");
        registered.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_configuration_the_ICacheService_registered_in_DI_is_the_default_cache()
    {
        var hostCache = new RecordingCacheService();
        var services = new ServiceCollection();
        services.AddSingleton<ICacheService>(hostCache);
        services.AddSingleton<IRateProvider, RateProvider>();
        services.AddAnnotationCaching(); // nothing configured -> the ICacheService from DI
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IRateProvider>().GetAsync("EUR", "NGN");

        hostCache.Keys.Should().Equal("rate:EUR:NGN");
    }

    public sealed class RecordingCacheService : ICacheService
    {
        public List<string> Keys { get; } = [];

        public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null)
        {
            Keys.Add(key);
            return factory();
        }

        public Task RemoveAsync(string key) => Task.CompletedTask;
    }
}

using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>
/// Plugging into cache infrastructure the host app already registers:
/// an IDistributedCache, or an IConnectionMultiplexer.
/// </summary>
public class ExistingCacheInfrastructureTests
{
    public record Account(string Id, int Version);

    public interface IAccountReader
    {
        [Cacheable("it-existing-account", 60, "id")]
        Task<Account> GetAsync(string id);

        [CacheEvict("it-existing-account", "id")]
        Task UpdateAsync(string id);

        int Calls { get; }
    }

    public class AccountReader : IAccountReader
    {
        private int _calls;
        private int _version;
        public int Calls => _calls;

        public Task<Account> GetAsync(string id)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new Account(id, _version));
        }

        public Task UpdateAsync(string id)
        {
            Interlocked.Increment(ref _version);
            return Task.CompletedTask;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The whole IDistributedCache integration: three one-line mappings.</summary>
    private static void MapDistributedCache(CachingOptions o) => o.UseCache<IDistributedCache>(
        get: (cache, key) => cache.GetAsync(key),
        set: (cache, key, value, ttl) => cache.SetAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }),
        remove: (cache, key) => cache.RemoveAsync(key));

    // ---------- IDistributedCache, mapped with UseCache ----------

    [Fact]
    public async Task UseCache_mapped_onto_IDistributedCache_stores_values_in_the_hosts_cache()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccountReader, AccountReader>();
        services.AddAnnotationCaching(MapDistributedCache);
        services.AddDistributedMemoryCache(); // host registers its cache AFTER caching is enabled
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IAccountReader>();
        var distributed = provider.GetRequiredService<IDistributedCache>();
        var id = NewId();

        var first = await reader.GetAsync(id);
        var second = await reader.GetAsync(id);

        second.Should().Be(first);
        reader.Calls.Should().Be(1);
        (await distributed.GetAsync($"it-existing-account:{id}")).Should().NotBeNull("the value lives in the host's cache");
    }

    [Fact]
    public async Task UseCache_mapped_onto_IDistributedCache_evicts_from_the_hosts_cache()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IAccountReader, AccountReader>();
        services.AddAnnotationCaching(MapDistributedCache);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IAccountReader>();
        var distributed = provider.GetRequiredService<IDistributedCache>();
        var id = NewId();

        await reader.GetAsync(id);
        await reader.UpdateAsync(id);

        (await distributed.GetAsync($"it-existing-account:{id}")).Should().BeNull();
        (await reader.GetAsync(id)).Version.Should().Be(1);
    }

    [RedisFact]
    public async Task UseCache_mapped_onto_AddStackExchangeRedisCache_respects_its_InstanceName()
    {
        var id = NewId();

        ServiceProvider BuildApp()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAccountReader, AccountReader>();
            services.AddAnnotationCaching(MapDistributedCache);
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = RedisFactAttribute.ConnectionString;
                o.InstanceName = "it-dist:";
            });
            return services.BuildServiceProvider();
        }

        using (var instanceA = BuildApp())
        {
            await instanceA.GetRequiredService<IAccountReader>().GetAsync(id);
        }

        using var instanceB = BuildApp();
        var readerB = instanceB.GetRequiredService<IAccountReader>();
        (await readerB.GetAsync(id)).Id.Should().Be(id);
        readerB.Calls.Should().Be(0, "served from Redis via the host's IDistributedCache");

        // RedisCache prefixes keys with InstanceName.
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.ConnectionString);
        var key = $"it-dist:it-existing-account:{id}";
        (await mux.GetDatabase().KeyExistsAsync(key)).Should().BeTrue();
        (await mux.GetDatabase().KeyTimeToLiveAsync(key)).Should().NotBeNull();

        await readerB.UpdateAsync(id);
        (await mux.GetDatabase().KeyExistsAsync(key)).Should().BeFalse();
    }

    // ---------- IConnectionMultiplexer, mapped with UseCache ----------

    [RedisFact]
    public async Task UseCache_mapped_onto_the_apps_multiplexer_registered_after_AddAnnotationCaching()
    {
        var appMultiplexer = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IAccountReader, AccountReader>();
        services.AddAnnotationCaching(o => o.UseRedis());
        services.AddSingleton<IConnectionMultiplexer>(appMultiplexer); // registered AFTER: resolved lazily
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IAccountReader>();
        var id = NewId();

        await reader.GetAsync(id);
        await reader.GetAsync(id);

        reader.Calls.Should().Be(1);
        (await appMultiplexer.GetDatabase().KeyExistsAsync($"it-existing-account:{id}")).Should().BeTrue();
    }
}

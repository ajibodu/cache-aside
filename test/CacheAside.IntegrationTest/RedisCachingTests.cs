using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>
/// End-to-end tests against a real Redis. Every test uses a fresh id, so keys never collide
/// between runs and expire on their own (60s TTL).
/// </summary>
public class RedisCachingTests
{
    public record Account(string Id, int Version, decimal Balance);

    public interface IAccountStore
    {
        [Cacheable("it-redis-account", 60, "id")]
        Task<Account> GetAsync(string id);

        [CacheEvict("it-redis-account", "id")]
        Task UpdateAsync(string id);

        int Calls { get; }
    }

    public class AccountStore : IAccountStore
    {
        private int _calls;
        private int _version;
        public int Calls => _calls;

        public async Task<Account> GetAsync(string id)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(10);
            return new Account(id, _version, 1234.56m);
        }

        public Task UpdateAsync(string id)
        {
            Interlocked.Increment(ref _version);
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore, AccountStore>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisFactAttribute.ConnectionString));
        services.AddAnnotationCaching(o => o.UseRedis());
        return services.BuildServiceProvider();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    [RedisFact]
    public async Task Value_is_stored_in_redis_with_ttl_and_second_call_is_a_cache_hit()
    {
        using var provider = BuildProvider();
        var store = provider.GetRequiredService<IAccountStore>();
        var db = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var id = NewId();

        var first = await store.GetAsync(id);
        var second = await store.GetAsync(id);

        second.Should().Be(first);
        store.Calls.Should().Be(1);

        var key = $"it-redis-account:{id}";
        (await db.KeyExistsAsync(key)).Should().BeTrue();
        (await db.KeyTimeToLiveAsync(key)).Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
    }

    [RedisFact]
    public async Task Complex_object_round_trips_through_json()
    {
        using var provider = BuildProvider();
        var store = provider.GetRequiredService<IAccountStore>();
        var id = NewId();

        await store.GetAsync(id);              // miss: written to Redis
        var fromRedis = await store.GetAsync(id); // hit: deserialized from Redis

        fromRedis.Should().Be(new Account(id, 0, 1234.56m));
    }

    [RedisFact]
    public async Task CacheEvict_deletes_the_redis_key()
    {
        using var provider = BuildProvider();
        var store = provider.GetRequiredService<IAccountStore>();
        var db = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var id = NewId();

        await store.GetAsync(id);
        await store.UpdateAsync(id);

        (await db.KeyExistsAsync($"it-redis-account:{id}")).Should().BeFalse();
        (await store.GetAsync(id)).Version.Should().Be(1);
        store.Calls.Should().Be(2);
    }

    [RedisFact]
    public async Task Cache_is_shared_between_separate_app_instances()
    {
        var id = NewId();

        using (var instanceA = BuildProvider())
        {
            await instanceA.GetRequiredService<IAccountStore>().GetAsync(id);
        }

        using var instanceB = BuildProvider();
        var storeB = instanceB.GetRequiredService<IAccountStore>();
        var result = await storeB.GetAsync(id);

        result.Id.Should().Be(id);
        storeB.Calls.Should().Be(0, "instance B should be served from the value instance A wrote to Redis");
    }
}

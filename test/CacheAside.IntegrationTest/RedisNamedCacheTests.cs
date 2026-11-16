using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>An in-memory default cache and a named Redis cache used side by side.</summary>
public class RedisNamedCacheTests
{
    public interface IFxService
    {
        [Cacheable("fx", 60, "pair", Cache = "redis")]
        Task<string> GetAsync(string pair);

        [Cacheable("fx", 60, "pair")] // same attribute prefix, default (memory) cache
        Task<string> GetLocalAsync(string pair);

        int Calls { get; }
    }

    public class FxService : IFxService
    {
        private int _calls;
        public int Calls => _calls;
        public Task<string> GetAsync(string pair) => Task.FromResult($"{pair}:{Interlocked.Increment(ref _calls)}");
        public Task<string> GetLocalAsync(string pair) => Task.FromResult($"{pair}:{Interlocked.Increment(ref _calls)}");
    }

    [RedisFact]
    public async Task Memory_default_and_named_redis_with_its_own_prefix_work_side_by_side()
    {
        var pair = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisFactAttribute.ConnectionString));
        services.AddSingleton<IFxService, FxService>();
        services.AddAnnotationCaching(o =>
        {
            o.UseMemory();
            o.AddCache("redis", c => c.UseRedis(keyPrefix: "it-multi:"));
        });

        await using (var provider = services.BuildServiceProvider())
        {
            var fx = provider.GetRequiredService<IFxService>();

            await fx.GetAsync(pair);
            await fx.GetAsync(pair);
            await fx.GetLocalAsync(pair);
            await fx.GetLocalAsync(pair);

            fx.Calls.Should().Be(2, "one miss per cache, then hits");
            provider.GetRequiredService<IMemoryCache>().TryGetValue($"fx:{pair}", out _).Should().BeTrue();
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.ConnectionString);
        (await mux.GetDatabase().KeyExistsAsync($"it-multi:fx:{pair}")).Should().BeTrue();
    }
}

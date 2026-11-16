using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>
/// The library has no stampede logic of its own. Plugged in with UseCache(getOrCreate, remove) or
/// UseCacheService, a cache that coordinates misses (HybridCache here) owns the miss flow and runs the
/// method once for N concurrent callers. With UseCache(get, set, remove) the library runs the miss flow,
/// so each concurrent miss runs the method.
/// </summary>
public class StampedeProtectionTests
{
    public interface ISlowService
    {
        [Cacheable("slow", 60, "id")]
        Task<string> GetAsync(string id);

        [CacheEvict("slow", "id")]
        Task ResetAsync(string id);

        int Calls { get; }
    }

    public class SlowService : ISlowService
    {
        private int _calls;
        public int Calls => _calls;

        public async Task<string> GetAsync(string id)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(200); // wide window for concurrent misses
            return $"value-{id}";
        }

        public Task ResetAsync(string id) => Task.CompletedTask;
    }

    private const int ConcurrentCallers = 50;

    private static async Task<string[]> HammerAsync(ISlowService service, string id) =>
        await Task.WhenAll(Enumerable.Range(0, ConcurrentCallers).Select(_ => Task.Run(() => service.GetAsync(id))));

    [Fact]
    public async Task HybridCache_via_UseCacheService_runs_the_method_once_for_concurrent_misses()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddSingleton<ISlowService, SlowService>();
        services.AddAnnotationCaching(o => o.UseHybridCache());
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ISlowService>();

        var results = await HammerAsync(service, "A");

        results.Should().OnlyContain(r => r == "value-A");
        service.Calls.Should().Be(1, "HybridCache coalesces concurrent misses for the same key");
    }

    [Fact]
    public async Task HybridCache_via_getOrCreate_delegate_runs_the_method_once_for_concurrent_misses()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddSingleton<ISlowService, SlowService>();
        services.AddAnnotationCaching(o => o.UseHybridCacheBytes());
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ISlowService>();

        var results = await HammerAsync(service, "A");

        results.Should().OnlyContain(r => r == "value-A");
        service.Calls.Should().Be(1, "getOrCreate hands the miss flow to HybridCache, which coalesces concurrent misses");

        await service.ResetAsync("A");
        await service.GetAsync("A");
        service.Calls.Should().Be(2, "remove still evicts");
    }

    [Fact]
    public async Task Get_set_mapping_has_no_stampede_protection()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddSingleton<ISlowService, SlowService>();
        services.AddAnnotationCaching(o => o.UseDistributedCache());
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ISlowService>();

        var results = await HammerAsync(service, "A");

        results.Should().OnlyContain(r => r == "value-A");
        service.Calls.Should().BeGreaterThan(1, "with get/set the library runs the miss flow, so concurrent misses each call the method");
    }

    [Fact]
    public async Task HybridCache_via_UseCacheService_supports_eviction()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddSingleton<ISlowService, SlowService>();
        services.AddAnnotationCaching(o => o.UseHybridCache());
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ISlowService>();

        await service.GetAsync("A");
        await service.ResetAsync("A");
        await service.GetAsync("A");

        service.Calls.Should().Be(2);
    }

    [RedisFact]
    public async Task HybridCache_with_redis_as_L2_shares_values_across_app_instances_with_one_call_per_stampede()
    {
        var id = Guid.NewGuid().ToString("N");

        ServiceProvider BuildApp()
        {
            var services = new ServiceCollection();
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = RedisFactAttribute.ConnectionString;
                o.InstanceName = "it-hybrid:";
            });
            services.AddHybridCache(); // picks up the IDistributedCache above as its L2
            services.AddSingleton<ISlowService, SlowService>();
            services.AddAnnotationCaching(o => o.UseHybridCache());
            return services.BuildServiceProvider();
        }

        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.ConnectionString);
        var key = $"it-hybrid:slow:{id}";

        await using (var instanceA = BuildApp())
        {
            var serviceA = instanceA.GetRequiredService<ISlowService>();
            await HammerAsync(serviceA, id);
            serviceA.Calls.Should().Be(1);

            // HybridCache writes L2 in the background after returning to the caller; wait for it to land.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!await mux.GetDatabase().KeyExistsAsync(key) && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        await using var instanceB = BuildApp();
        var serviceB = instanceB.GetRequiredService<ISlowService>();
        (await serviceB.GetAsync(id)).Should().Be($"value-{id}");
        serviceB.Calls.Should().Be(0, "instance B reads the value instance A stored in Redis (L2)");

        (await mux.GetDatabase().KeyExistsAsync(key)).Should().BeTrue();
    }
}

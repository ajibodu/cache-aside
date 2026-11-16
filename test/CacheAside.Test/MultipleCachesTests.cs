using System.Collections.Concurrent;
using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>Several caches side by side, selected per method with Cache = "name".</summary>
public class MultipleCachesTests
{
    /// <summary>A byte store standing in for any app cache, so tests can see exactly what lands where.</summary>
    public class ByteStore
    {
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();
    }

    public class SharedStore : ByteStore;

    private static void MapShared(CacheOptions c) => c.UseCache<SharedStore>(
        get: (s, key) => Task.FromResult(s.Entries.TryGetValue(key, out var v) ? v : null),
        set: (s, key, value, _) => { s.Entries[key] = value; return Task.CompletedTask; },
        remove: (s, key) => { s.Entries.TryRemove(key, out _); return Task.CompletedTask; },
        keyPrefix: "app:");

    public interface ICatalogService
    {
        [Cacheable("account", 60, "id")] // default cache
        Task<string> GetAccountAsync(string id);

        [Cacheable("rate", 60, "pair", Cache = "shared")]
        Task<string> GetRateAsync(string pair);

        [CacheEvict("rate", "pair", Cache = "shared")]
        Task RefreshRateAsync(string pair);

        [Cacheable("item", 60, "id", Cache = "local-a")]
        Task<string> GetFromAAsync(string id);

        [Cacheable("item", 60, "id", Cache = "local-b")]
        Task<string> GetFromBAsync(string id);

        int Calls { get; }
    }

    public class CatalogService : ICatalogService
    {
        private int _calls;
        public int Calls => _calls;

        private Task<string> Next(string label) =>
            Task.FromResult($"{label}#{Interlocked.Increment(ref _calls)}");

        public Task<string> GetAccountAsync(string id) => Next($"account-{id}");
        public Task<string> GetRateAsync(string pair) => Next($"rate-{pair}");
        public Task RefreshRateAsync(string pair) => Task.CompletedTask;
        public Task<string> GetFromAAsync(string id) => Next($"a-{id}");
        public Task<string> GetFromBAsync(string id) => Next($"b-{id}");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<SharedStore>();
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddAnnotationCaching(o =>
        {
            o.UseMemory(keyPrefix: "app:"); // default cache: in-memory with a prefix
            o.AddCache("shared", MapShared);
            o.AddCache("local-a", c => c.UseIsolatedMemory());
            o.AddCache("local-b", c => c.UseIsolatedMemory());
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Each_method_uses_the_cache_named_in_its_attribute_with_that_caches_prefix()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<ICatalogService>();
        var defaultCache = provider.GetRequiredService<IMemoryCache>();
        var shared = provider.GetRequiredService<SharedStore>();

        await service.GetAccountAsync("ACC-1");
        await service.GetRateAsync("USDNGN");

        defaultCache.TryGetValue("app:account:ACC-1", out _).Should().BeTrue("no Cache name -> default cache + default prefix");
        shared.Entries.Keys.Should().Equal("app:rate:USDNGN");
        defaultCache.TryGetValue("app:rate:USDNGN", out _).Should().BeFalse("named-cache entries don't leak into the default cache");
    }

    [Fact]
    public async Task Named_cache_serves_hits()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var first = await service.GetRateAsync("USDNGN");
        var second = await service.GetRateAsync("USDNGN");

        second.Should().Be(first);
        service.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Evict_targets_the_named_cache()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<ICatalogService>();
        var shared = provider.GetRequiredService<SharedStore>();

        var before = await service.GetRateAsync("USDNGN");
        await service.RefreshRateAsync("USDNGN");

        shared.Entries.Should().BeEmpty();
        (await service.GetRateAsync("USDNGN")).Should().NotBe(before);
    }

    [Fact]
    public async Task Named_memory_caches_are_isolated_from_each_other_even_with_identical_keys()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var a = await service.GetFromAAsync("1"); // key item:1 in local-a
        var b = await service.GetFromBAsync("1"); // key item:1 in local-b

        a.Should().StartWith("a-1");
        b.Should().StartWith("b-1", "local-b must not return local-a's entry for the same key");
        (await service.GetFromAAsync("1")).Should().Be(a);
        (await service.GetFromBAsync("1")).Should().Be(b);
        service.Calls.Should().Be(2);
    }

    // ---------- startup validation ----------

    public interface IUnknownCacheService
    {
        [Cacheable("x", 60, "id", Cache = "does-not-exist")]
        Task<string> GetAsync(string id);
    }

    public class UnknownCacheService : IUnknownCacheService
    {
        public Task<string> GetAsync(string id) => Task.FromResult(id);
    }

    [Fact]
    public void Unknown_cache_name_fails_at_registration_and_lists_the_registered_caches()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUnknownCacheService, UnknownCacheService>();

        var act = () => services.AddAnnotationCaching(o => o.AddCache("shared", c => c.UseIsolatedMemory()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'does-not-exist' is not registered*'shared'*");
    }

    [Fact]
    public void Adding_the_same_cache_name_twice_fails()
    {
        var act = () => new ServiceCollection().AddAnnotationCaching(o => o
            .AddCache("dup", c => c.UseIsolatedMemory())
            .AddCache("dup", c => c.UseIsolatedMemory()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'dup'*already*");
    }

    [Fact]
    public void Named_cache_without_a_backend_fails_at_registration()
    {
        var act = () => new ServiceCollection().AddAnnotationCaching(o => o.AddCache("broken", _ => { }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'broken' has no backend*UseCache*");
    }

    [Fact]
    public void Calling_AddAnnotationCaching_twice_fails()
    {
        var services = new ServiceCollection();
        services.AddAnnotationCaching();

        var act = () => services.AddAnnotationCaching();

        act.Should().Throw<InvalidOperationException>().WithMessage("*only be called once*");
    }

    // ---------- UseCacheService<T> and ownership ----------

    public sealed class DisposableCacheService : ICacheService, IDisposable
    {
        public static int Disposed;
        public List<string> Keys { get; } = [];

        public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? duration = null)
        {
            Keys.Add(key);
            return factory();
        }

        public Task RemoveAsync(string key) => Task.CompletedTask;
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    public interface IAuditService
    {
        [Cacheable("audit", 60, "id", Cache = "custom")]
        Task<string> GetAsync(string id);
    }

    public class AuditService : IAuditService
    {
        public Task<string> GetAsync(string id) => Task.FromResult(id);
    }

    [Fact]
    public async Task Named_cache_can_use_an_ICacheService_class_which_is_created_lazily_and_disposed_with_the_container()
    {
        var disposedBefore = DisposableCacheService.Disposed;
        var services = new ServiceCollection();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddAnnotationCaching(o => o.AddCache("custom", c => c.UseCacheService<DisposableCacheService>()));

        await using (var provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<IAuditService>().GetAsync("A1");
        }

        DisposableCacheService.Disposed.Should().Be(disposedBefore + 1, "the library created it, so the library disposes it");
    }
}

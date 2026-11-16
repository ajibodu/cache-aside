using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>
/// End-to-end tests for the library: real ServiceCollection, real Castle proxy,
/// real CachingInterceptor and in-memory ICacheService. Nothing is mocked.
/// </summary>
public class CachingPipelineTests
{
    public record Account(string Id, int Version);

    public interface IAccountService
    {
        [Cacheable("account", 60, "id")]
        Task<Account> GetAsync(string id);

        [Cacheable("account-short", 1, "id")]
        Task<Account> GetShortLivedAsync(string id);

        [CacheEvict("account", "id")]
        Task UpdateAsync(string id);

        int Calls { get; }
    }

    public class AccountService : IAccountService
    {
        private int _calls;
        private int _version;
        public int Calls => _calls;

        public async Task<Account> GetAsync(string id)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20); // force a genuinely asynchronous completion
            return new Account(id, _version);
        }

        public async Task<Account> GetShortLivedAsync(string id)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20);
            return new Account(id, _version);
        }

        public Task UpdateAsync(string id)
        {
            Interlocked.Increment(ref _version);
            return Task.CompletedTask;
        }
    }

    // Attributes declared on the implementation only (not on the interface).
    public interface IRateService
    {
        Task<decimal> GetRateAsync(string currency);
        int Calls { get; }
    }

    public class RateService : IRateService
    {
        private int _calls;
        public int Calls => _calls;

        [Cacheable("rate", 60, "currency")]
        public async Task<decimal> GetRateAsync(string currency)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20);
            return 1.5m;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IRateService, RateService>();
        services.AddAnnotationCaching(o => o.UseMemory()); // must come after the registrations
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Service_with_annotations_is_resolved_as_a_proxy()
    {
        using var provider = BuildProvider();

        var service = provider.GetRequiredService<IAccountService>();

        service.Should().NotBeOfType<AccountService>();
        Castle.DynamicProxy.ProxyUtil.IsProxy(service).Should().BeTrue();
    }

    [Fact]
    public async Task Cacheable_method_returns_the_underlying_value()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IAccountService>();

        var result = await service.GetAsync("acc-1");

        result.Should().Be(new Account("acc-1", 0));
    }

    [Fact]
    public async Task Second_call_with_same_key_is_served_from_cache_without_invoking_the_method()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IAccountService>();

        var first = await service.GetAsync("acc-1");
        var second = await service.GetAsync("acc-1");

        second.Should().Be(first);
        service.Calls.Should().Be(1, "the second call should be a cache hit");
    }

    [Fact]
    public async Task Different_key_arguments_are_cached_separately()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IAccountService>();

        var a = await service.GetAsync("acc-1");
        var b = await service.GetAsync("acc-2");

        a.Id.Should().Be("acc-1");
        b.Id.Should().Be("acc-2");
        service.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CacheEvict_removes_the_entry_so_the_next_call_hits_the_method()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IAccountService>();

        var before = await service.GetAsync("acc-1");
        await service.UpdateAsync("acc-1");
        var after = await service.GetAsync("acc-1");

        before.Version.Should().Be(0);
        after.Version.Should().Be(1, "eviction should force a fresh value");
        service.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Entry_expires_after_the_configured_duration()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IAccountService>();

        await service.GetShortLivedAsync("acc-1");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await service.GetShortLivedAsync("acc-1");

        service.Calls.Should().Be(2);
    }

    public interface IBalanceService
    {
        [Cacheable("balance", 60, "accountId")]
        Task<decimal> GetAsync(string accountId);

        // Different parameter name from GetAsync: keys are positional, so eviction still matches.
        [CacheEvict("balance", "id")]
        Task<bool> DebitAsync(string id, decimal amount);
    }

    public class BalanceService : IBalanceService
    {
        private decimal _balance = 100m;
        public Task<decimal> GetAsync(string accountId) => Task.FromResult(_balance);

        public Task<bool> DebitAsync(string id, decimal amount)
        {
            _balance -= amount;
            return Task.FromResult(true);
        }
    }

    public interface IMisconfiguredService
    {
        [Cacheable("bad", 60, "doesNotExist")]
        Task<int> GetAsync(string id);
    }

    public class MisconfiguredService : IMisconfiguredService
    {
        public Task<int> GetAsync(string id) => Task.FromResult(1);
    }

    [Fact]
    public async Task CacheEvict_on_a_method_returning_a_value_passes_the_value_through_and_evicts()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IBalanceService, BalanceService>();
        services.AddAnnotationCaching(o => o.UseMemory());
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IBalanceService>();

        (await service.GetAsync("acc-1")).Should().Be(100m);
        (await service.DebitAsync("acc-1", 30m)).Should().BeTrue();
        (await service.GetAsync("acc-1")).Should().Be(70m);
    }

    [Fact]
    public void Unknown_key_argument_fails_at_registration_not_at_first_call()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IMisconfiguredService, MisconfiguredService>();

        var act = () => services.AddAnnotationCaching(o => o.UseMemory());

        act.Should().Throw<InvalidOperationException>().WithMessage("*doesNotExist*");
    }

    [Fact]
    public void Registration_order_is_preserved_so_last_registration_still_wins()
    {
        var services = new ServiceCollection(); // nothing is resolved, so no IMemoryCache needed
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IRateService, RateService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddAnnotationCaching(o => o.UseMemory());

        services.Where(d => d.ServiceType == typeof(IAccountService)).Should().HaveCount(2);
        services.Select(d => d.ServiceType).ToList().IndexOf(typeof(IRateService)).Should().Be(1);
    }

    [Fact]
    public async Task Attribute_on_implementation_only_is_honoured()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IRateService>();

        var first = await service.GetRateAsync("NGN");
        var second = await service.GetRateAsync("NGN");

        first.Should().Be(1.5m);
        second.Should().Be(1.5m);
        service.Calls.Should().Be(1);
    }
}

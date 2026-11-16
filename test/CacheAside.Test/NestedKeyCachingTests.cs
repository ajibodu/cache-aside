using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>Key arguments that are dotted paths into a parameter, e.g. "request.Account.Id".</summary>
public class NestedKeyCachingTests
{
    public record AccountRef(string Id, int? BranchCode);
    public record QuoteRequest(AccountRef? Account, string Currency, decimal Amount);

    public interface IQuoteService
    {
        // Amount is deliberately not part of the key.
        [Cacheable("quote", 60, "request.Account.Id", "request.Currency")]
        Task<string> GetQuoteAsync(QuoteRequest request);

        [Cacheable("branch", 60, "request.Account.BranchCode")]
        Task<string> GetBranchAsync(QuoteRequest request);

        // Plain parameters on the evict side, nested on the cache side: same prefix, same order, same values.
        [CacheEvict("quote", "accountId", "currency")]
        Task ResetAsync(string accountId, string currency);

        int Calls { get; }
    }

    public class QuoteService : IQuoteService
    {
        private int _calls;
        public int Calls => _calls;

        public Task<string> GetQuoteAsync(QuoteRequest request)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult($"{request.Account?.Id}/{request.Currency}/{n}");
        }

        public Task<string> GetBranchAsync(QuoteRequest request)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult($"branch-{request.Account?.BranchCode}");
        }

        public Task ResetAsync(string accountId, string currency) => Task.CompletedTask;
    }

    public interface IBadPathService
    {
        [Cacheable("bad", 60, "request.Account.DoesNotExist")]
        Task<string> GetAsync(QuoteRequest request);
    }

    public class BadPathService : IBadPathService
    {
        public Task<string> GetAsync(QuoteRequest request) => Task.FromResult("x");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IQuoteService, QuoteService>();
        services.AddAnnotationCaching(o => o.UseMemory());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Key_is_built_from_nested_property_values()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IQuoteService>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        await service.GetQuoteAsync(new QuoteRequest(new AccountRef("ACC-1", 7), "NGN", 100m));

        cache.TryGetValue("quote:ACC-1:NGN", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Properties_outside_the_key_do_not_affect_caching()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IQuoteService>();

        var first = await service.GetQuoteAsync(new QuoteRequest(new AccountRef("ACC-1", 7), "NGN", 100m));
        var second = await service.GetQuoteAsync(new QuoteRequest(new AccountRef("ACC-1", 9), "NGN", 999m));
        var otherCurrency = await service.GetQuoteAsync(new QuoteRequest(new AccountRef("ACC-1", 7), "USD", 100m));

        second.Should().Be(first, "only Account.Id and Currency are in the key");
        otherCurrency.Should().NotBe(first);
        service.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Null_link_in_the_path_produces_a_null_segment_instead_of_throwing()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IQuoteService>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        await service.GetQuoteAsync(new QuoteRequest(null, "NGN", 100m));
        await service.GetBranchAsync(new QuoteRequest(new AccountRef("ACC-1", null), "NGN", 1m));

        cache.TryGetValue("quote:null:NGN", out _).Should().BeTrue();
        cache.TryGetValue("branch:null", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Nullable_value_type_in_the_path_uses_its_value()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IQuoteService>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        await service.GetBranchAsync(new QuoteRequest(new AccountRef("ACC-1", 42), "NGN", 1m));

        cache.TryGetValue("branch:42", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Evict_with_plain_parameters_clears_an_entry_cached_with_nested_ones_when_values_match()
    {
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<IQuoteService>();
        var request = new QuoteRequest(new AccountRef("ACC-1", 7), "NGN", 100m);

        var before = await service.GetQuoteAsync(request);
        await service.ResetAsync("ACC-2", "NGN");           // different value: must NOT evict ACC-1
        var stillCached = await service.GetQuoteAsync(request);
        await service.ResetAsync("ACC-1", "NGN");           // same values: evicts
        var after = await service.GetQuoteAsync(request);

        stillCached.Should().Be(before);
        after.Should().NotBe(before);
        service.Calls.Should().Be(2);
    }

    [Fact]
    public void Unknown_member_in_the_path_fails_at_registration()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IBadPathService, BadPathService>();

        var act = () => services.AddAnnotationCaching(o => o.UseMemory());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*request.Account.DoesNotExist*AccountRef*DoesNotExist*");
    }
}

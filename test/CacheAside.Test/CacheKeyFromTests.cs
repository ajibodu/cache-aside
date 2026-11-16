using System.Collections.Concurrent;
using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>[CacheKeyFrom&lt;T&gt;]: key values from injected services, e.g. one cache entry per user.</summary>
public class CacheKeyFromTests
{
    /// <summary>Stands in for CurrentUserProperties: scoped, filled per request.</summary>
    public class UserContext
    {
        public string? StaffId { get; set; }
        public Tenant? Tenant { get; set; }
    }

    public class Tenant
    {
        public string Code { get; set; } = "";
    }

    /// <summary>Same for every request, so a singleton may use it.</summary>
    public class AppInfo
    {
        public string Region { get; set; } = "eu";
    }

    public class Calls
    {
        public int Count;
    }

    public class Store
    {
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();
    }

    public interface IDashboardService
    {
        [Cacheable("dashboard", 60)]
        [CacheKeyFrom<UserContext>(nameof(UserContext.StaffId))]
        Task<string> GetMyDashboardAsync();

        [Cacheable("report", 60, nameof(year))]
        [CacheKeyFrom<UserContext>("Tenant.Code")]
        [CacheKeyFrom<AppInfo>(nameof(AppInfo.Region))]
        Task<string> GetReportAsync(int year);

        [CacheEvict("dashboard")]
        [CacheKeyFrom<UserContext>(nameof(UserContext.StaffId))]
        Task ResetMyDashboardAsync();
    }

    public class DashboardService(UserContext user, Calls calls) : IDashboardService
    {
        public Task<string> GetMyDashboardAsync()
        {
            Interlocked.Increment(ref calls.Count);
            return Task.FromResult($"dashboard of {user.StaffId}");
        }

        public Task<string> GetReportAsync(int year)
        {
            Interlocked.Increment(ref calls.Count);
            return Task.FromResult($"{user.Tenant?.Code} {year}");
        }

        public Task ResetMyDashboardAsync() => Task.CompletedTask;
    }

    public interface IRegionService
    {
        [Cacheable("region", 60)]
        [CacheKeyFrom<AppInfo>(nameof(AppInfo.Region))]
        Task<string> GetAsync();
    }

    public class RegionService(AppInfo app) : IRegionService
    {
        public Task<string> GetAsync() => Task.FromResult(app.Region);
    }

    public interface IOrphanService
    {
        [CacheKeyFrom<UserContext>(nameof(UserContext.StaffId))] // no [Cacheable]/[CacheEvict]
        Task<string> GetAsync();
    }

    public class OrphanService : IOrphanService
    {
        public Task<string> GetAsync() => Task.FromResult("x");
    }

    public interface ISplitService
    {
        [CacheKeyFrom<UserContext>(nameof(UserContext.StaffId))] // on the interface...
        Task<string> GetAsync();
    }

    public class SplitService : ISplitService
    {
        [Cacheable("split", 60)] // ...but [Cacheable] on the implementation
        public Task<string> GetAsync() => Task.FromResult("x");
    }

    public interface IBadMemberService
    {
        [Cacheable("bad", 60)]
        [CacheKeyFrom<UserContext>("StaffNumber")]
        Task<string> GetAsync();
    }

    public class BadMemberService : IBadMemberService
    {
        public Task<string> GetAsync() => Task.FromResult("x");
    }

    private static void UseStore(CacheOptions c) => c.UseCache<Store>(
        get: (s, key) => Task.FromResult(s.Entries.TryGetValue(key, out var v) ? v : null),
        set: (s, key, value, _) => { s.Entries[key] = value; return Task.CompletedTask; },
        remove: (s, key) => { s.Entries.TryRemove(key, out _); return Task.CompletedTask; });

    private static ServiceProvider Build(ServiceLifetime dashboardLifetime = ServiceLifetime.Scoped)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<Store>();
        services.AddSingleton<Calls>();
        services.AddSingleton<AppInfo>();
        services.AddScoped<UserContext>();
        services.Add(new ServiceDescriptor(typeof(IDashboardService), typeof(DashboardService), dashboardLifetime));
        services.AddAnnotationCaching(UseStore);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Runs <paramref name="call"/> in its own scope (one "request") as the given user.</summary>
    private static async Task<T> AsUser<T>(ServiceProvider provider, string? staffId, Func<IDashboardService, Task<T>> call, string? tenant = "T1")
    {
        await using var scope = provider.CreateAsyncScope();
        var user = scope.ServiceProvider.GetRequiredService<UserContext>();
        user.StaffId = staffId;
        user.Tenant = tenant == null ? null : new Tenant { Code = tenant };
        return await call(scope.ServiceProvider.GetRequiredService<IDashboardService>());
    }

    private static Task AsUser(ServiceProvider provider, string staffId, Func<IDashboardService, Task> call) =>
        AsUser(provider, staffId, async s => { await call(s); return 0; });

    [Fact]
    public async Task Each_user_gets_their_own_entry()
    {
        await using var provider = Build();
        var calls = provider.GetRequiredService<Calls>();

        var alice1 = await AsUser(provider, "alice", s => s.GetMyDashboardAsync());
        var alice2 = await AsUser(provider, "alice", s => s.GetMyDashboardAsync());
        var bob = await AsUser(provider, "bob", s => s.GetMyDashboardAsync());

        alice1.Should().Be("dashboard of alice");
        alice2.Should().Be("dashboard of alice");
        bob.Should().Be("dashboard of bob", "bob must never get alice's cached dashboard");
        calls.Count.Should().Be(2);
        provider.GetRequiredService<Store>().Entries.Keys.Should().BeEquivalentTo("dashboard:alice", "dashboard:bob");
    }

    [Fact]
    public async Task Evict_removes_only_the_current_users_entry()
    {
        await using var provider = Build();
        var calls = provider.GetRequiredService<Calls>();
        await AsUser(provider, "alice", s => s.GetMyDashboardAsync());
        await AsUser(provider, "bob", s => s.GetMyDashboardAsync());

        await AsUser(provider, "alice", s => s.ResetMyDashboardAsync());

        provider.GetRequiredService<Store>().Entries.Keys.Should().BeEquivalentTo("dashboard:bob");
        await AsUser(provider, "bob", s => s.GetMyDashboardAsync());
        await AsUser(provider, "alice", s => s.GetMyDashboardAsync());
        calls.Count.Should().Be(3, "only alice's dashboard was rebuilt");
    }

    [Fact]
    public async Task Service_values_come_first_ordered_by_type_name_then_the_key_arguments()
    {
        await using var provider = Build();

        await AsUser(provider, "alice", s => s.GetReportAsync(2026), tenant: "T1");

        // AppInfo sorts before UserContext, whatever order the attributes are written in.
        provider.GetRequiredService<Store>().Entries.Keys.Should().BeEquivalentTo("report:eu:T1:2026");
    }

    [Fact]
    public async Task A_null_value_fails_the_call_without_running_the_method()
    {
        await using var provider = Build();
        var calls = provider.GetRequiredService<Calls>();

        var noUser = () => AsUser(provider, null, s => s.GetMyDashboardAsync());
        var noTenant = () => AsUser(provider, "alice", s => s.GetReportAsync(2026), tenant: null);

        await noUser.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GetMyDashboardAsync*[CacheKeyFrom<UserContext>(\"StaffId\")] is null*");
        await noTenant.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*[CacheKeyFrom<UserContext>(\"Tenant.Code\")] is null*");
        calls.Count.Should().Be(0);
        provider.GetRequiredService<Store>().Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_value_on_evict_fails_without_running_the_method()
    {
        await using var provider = Build();

        var evict = async () =>
        {
            await using var scope = provider.CreateAsyncScope(); // no user set: StaffId is null
            await scope.ServiceProvider.GetRequiredService<IDashboardService>().ResetMyDashboardAsync();
        };

        await evict.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ResetMyDashboardAsync*is null*");
    }

    [Fact]
    public async Task Transient_services_work_inside_a_request_scope()
    {
        await using var provider = Build(ServiceLifetime.Transient);

        (await AsUser(provider, "alice", s => s.GetMyDashboardAsync())).Should().Be("dashboard of alice");
        (await AsUser(provider, "bob", s => s.GetMyDashboardAsync())).Should().Be("dashboard of bob");
    }

    [Fact]
    public void Singleton_service_reading_a_scoped_service_fails_at_startup()
    {
        var act = () => Build(ServiceLifetime.Singleton);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IDashboardService is registered as a singleton, so it can't use [CacheKeyFrom<UserContext>]: UserContext is scoped*");
    }

    [Fact]
    public async Task Singleton_service_reading_a_singleton_service_is_allowed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Store>();
        services.AddSingleton(new AppInfo { Region = "ng" });
        services.AddSingleton<IRegionService, RegionService>();
        services.AddAnnotationCaching(UseStore);
        await using var provider = services.BuildServiceProvider();

        (await provider.GetRequiredService<IRegionService>().GetAsync()).Should().Be("ng");
        provider.GetRequiredService<Store>().Entries.Keys.Should().BeEquivalentTo("region:ng");
    }

    [Fact]
    public void Unregistered_service_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Store>();
        services.AddScoped<IRegionService, RegionService>(); // AppInfo never registered

        var act = () => services.AddAnnotationCaching(UseStore);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*[CacheKeyFrom<AppInfo>] reads *AppInfo, which is not registered*");
    }

    [Fact]
    public void Unknown_member_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<UserContext>();
        services.AddScoped<IBadMemberService, BadMemberService>();

        var act = () => services.AddAnnotationCaching(UseStore);

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no public property or field 'StaffNumber'*");
    }

    [Fact]
    public void CacheKeyFrom_without_Cacheable_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<UserContext>();
        services.AddScoped<IOrphanService, OrphanService>();

        var act = () => services.AddAnnotationCaching(UseStore);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IOrphanService.GetAsync*only works together with [Cacheable] or [CacheEvict]*");
    }

    [Fact]
    public void CacheKeyFrom_on_a_different_method_than_Cacheable_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<UserContext>();
        services.AddScoped<ISplitService, SplitService>();

        var act = () => services.AddAnnotationCaching(UseStore);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be on the same method as its [Cacheable]/[CacheEvict]*");
    }
}

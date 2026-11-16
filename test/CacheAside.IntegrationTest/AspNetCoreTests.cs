using System.Net.Http.Json;
using CacheAside;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.IntegrationTest;

/// <summary>A real web app over HTTP: request-scoped services, a per-request user and the startup check.</summary>
public class AspNetCoreTests
{
    /// <summary>Filled per request from a header, the way an app fills it from the signed-in user.</summary>
    public class CurrentUser
    {
        public string? Id { get; set; }
    }

    public class CallCounter
    {
        public int Count;
    }

    public record Dashboard(string UserId, int Build);

    public interface IDashboardService
    {
        [Cacheable("dashboard", 60)]
        [CacheKeyFrom<CurrentUser>(nameof(CurrentUser.Id))]
        Task<Dashboard> GetMyDashboardAsync();

        [CacheEvict("dashboard")]
        [CacheKeyFrom<CurrentUser>(nameof(CurrentUser.Id))]
        Task ResetMyDashboardAsync();
    }

    public class DashboardService(CurrentUser user, CallCounter counter) : IDashboardService
    {
        public Task<Dashboard> GetMyDashboardAsync() =>
            Task.FromResult(new Dashboard(user.Id!, Interlocked.Increment(ref counter.Count)));

        public Task ResetMyDashboardAsync() => Task.CompletedTask;
    }

    private static WebApplication BuildApp(bool registerServiceLate = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<CallCounter>();
        builder.Services.AddScoped<CurrentUser>();
        if (!registerServiceLate) builder.Services.AddScoped<IDashboardService, DashboardService>();
        builder.Services.AddAnnotationCaching(o => o.UseMemory());
        if (registerServiceLate) builder.Services.AddScoped<IDashboardService, DashboardService>();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.RequestServices.GetRequiredService<CurrentUser>().Id = context.Request.Headers["X-User"].FirstOrDefault();
            return next(context);
        });
        app.MapGet("/dashboard", (IDashboardService service) => service.GetMyDashboardAsync());
        app.MapDelete("/dashboard", async (IDashboardService service) =>
        {
            await service.ResetMyDashboardAsync();
            return Results.NoContent();
        });
        return app;
    }

    private static HttpClient ClientFor(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<Dashboard> GetDashboard(HttpClient client, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.Add("X-User", user);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Dashboard>())!;
    }

    [Fact]
    public async Task Each_user_gets_their_own_cached_response_and_can_evict_only_their_own()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = ClientFor(app);

        var alice1 = await GetDashboard(client, "alice");
        var alice2 = await GetDashboard(client, "alice");
        var bob = await GetDashboard(client, "bob");

        alice1.Should().Be(new Dashboard("alice", 1));
        alice2.Should().Be(alice1, "served from the cache");
        bob.Should().Be(new Dashboard("bob", 2), "bob never gets alice's entry");

        using var reset = new HttpRequestMessage(HttpMethod.Delete, "/dashboard");
        reset.Headers.Add("X-User", "alice");
        (await client.SendAsync(reset)).EnsureSuccessStatusCode();

        (await GetDashboard(client, "alice")).Should().Be(new Dashboard("alice", 3), "alice's entry was evicted");
        (await GetDashboard(client, "bob")).Should().Be(bob, "bob's entry is untouched");
        await app.StopAsync();
    }

    [Fact]
    public async Task App_does_not_start_when_a_cached_service_is_registered_after_AddAnnotationCaching()
    {
        await using var app = BuildApp(registerServiceLate: true);

        var start = () => app.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IDashboardService was registered after AddAnnotationCaching*");
    }
}

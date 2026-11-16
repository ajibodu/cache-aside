using CacheAside;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CacheAside.IntegrationTest;

/// <summary>An annotated service registered after AddAnnotationCaching fails host startup.</summary>
public class LateRegistrationTests
{
    public interface IFxRateService
    {
        [Cacheable("fx", 60, nameof(currency))]
        Task<decimal> GetRateAsync(string currency);
    }

    public class FxRateService : IFxRateService
    {
        public Task<decimal> GetRateAsync(string currency) => Task.FromResult(1500m);
    }

    public interface IPlainService
    {
        Task<int> GetAsync();
    }

    public class PlainService : IPlainService
    {
        public Task<int> GetAsync() => Task.FromResult(1);
    }

    /// <summary>Stands in for any other hosted service, e.g. the web server.</summary>
    public class StartRecorder : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HostApplicationBuilder NewHost() => Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

    [Fact]
    public async Task Annotated_service_registered_after_AddAnnotationCaching_fails_startup()
    {
        var builder = NewHost();
        builder.Services.AddAnnotationCaching(o => o.UseIsolatedMemory());
        builder.Services.AddScoped<IFxRateService, FxRateService>(); // too late
        using var host = builder.Build();

        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IFxRateService.GetRateAsync: IFxRateService was registered after AddAnnotationCaching, so it isn't cached*");
    }

    [Fact]
    public async Task Registered_before_AddAnnotationCaching_starts_and_caches()
    {
        var builder = NewHost();
        builder.Services.AddScoped<IFxRateService, FxRateService>();
        builder.Services.AddAnnotationCaching(o => o.UseIsolatedMemory());
        using var host = builder.Build();

        await host.StartAsync();

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IFxRateService>().GetType().Should().NotBe<FxRateService>("it is the caching proxy");
        await host.StopAsync();
    }

    [Fact]
    public async Task Replacing_a_proxied_service_afterwards_fails_startup()
    {
        var builder = NewHost();
        builder.Services.AddScoped<IFxRateService, FxRateService>();
        builder.Services.AddAnnotationCaching(o => o.UseIsolatedMemory());
        builder.Services.Replace(ServiceDescriptor.Scoped<IFxRateService, FxRateService>()); // drops the proxy
        using var host = builder.Build();

        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*registered after AddAnnotationCaching*");
    }

    [Fact]
    public async Task Services_without_annotations_can_be_registered_afterwards()
    {
        var builder = NewHost();
        builder.Services.AddScoped<IFxRateService, FxRateService>();
        builder.Services.AddAnnotationCaching(o => o.UseIsolatedMemory());
        builder.Services.AddScoped<IPlainService, PlainService>();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task The_check_runs_before_other_hosted_services_start()
    {
        var recorder = new StartRecorder();
        var builder = NewHost();
        builder.Services.AddSingleton<IHostedService>(recorder); // registered first, like the web server
        builder.Services.AddAnnotationCaching(o => o.UseIsolatedMemory());
        builder.Services.AddScoped<IFxRateService, FxRateService>();
        using var host = builder.Build();

        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>();
        recorder.Started.Should().BeFalse("the app must not start serving with an uncached service");
    }
}

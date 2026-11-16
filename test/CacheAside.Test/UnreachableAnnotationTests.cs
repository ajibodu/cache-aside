using System.Reflection;
using System.Reflection.Emit;
using CacheAside;
using CacheAside.Internal.Proxying;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Test;

/// <summary>Annotations that could never run fail at startup instead of silently doing nothing.</summary>
public class UnreachableAnnotationTests
{
    public interface IPriceService
    {
        [Cacheable("price", 60, "id")]
        Task<int> GetAsync(int id);
    }

    public class PriceService : IPriceService
    {
        public int Calls;
        public Task<int> GetAsync(int id) { Calls++; return Task.FromResult(id); }
    }

    public interface ITariffService
    {
        Task<int> GetAsync(int id);
    }

    public class TariffService : ITariffService
    {
        public Task<int> GetAsync(int id) => Task.FromResult(id);

        [Cacheable("tariff", 60, "id")] // not on ITariffService: callers can't reach it through the proxy
        public Task<int> GetExtraAsync(int id) => Task.FromResult(id);
    }

    private static Action Configure(Action<IServiceCollection> register) => () =>
    {
        var services = new ServiceCollection();
        register(services);
        services.AddAnnotationCaching(o => o.UseIsolatedMemory());
    };

    [Fact]
    public void Keyed_registration_of_an_annotated_service_throws()
    {
        Configure(s => s.AddKeyedScoped<IPriceService, PriceService>("eu"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*IPriceService.GetAsync: it is registered as a keyed service*");
    }

    [Fact]
    public void Factory_registration_of_an_annotated_service_throws()
    {
        Configure(s => s.AddScoped<IPriceService>(_ => new PriceService()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*IPriceService.GetAsync: it is registered with a factory*AddScoped<Interface, Implementation>()*");
    }

    [Fact]
    public void Instance_registration_of_an_annotated_service_throws()
    {
        Configure(s => s.AddSingleton<IPriceService>(new PriceService()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*IPriceService.GetAsync: it is registered as an instance*");
    }

    [Fact]
    public void Class_registration_of_an_annotated_service_throws()
    {
        Configure(s => s.AddScoped<PriceService>())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*IPriceService.GetAsync: it is registered as the class PriceService*AddScoped<IYourService, PriceService>()*");
    }

    [Fact]
    public async Task Class_registration_that_is_the_proxy_target_is_allowed()
    {
        var services = new ServiceCollection();
        services.AddScoped<PriceService>();
        services.AddScoped<IPriceService, PriceService>();
        services.AddAnnotationCaching(o => o.UseIsolatedMemory());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var price = scope.ServiceProvider.GetRequiredService<IPriceService>();
        (await price.GetAsync(1)).Should().Be(1);
        (await price.GetAsync(1)).Should().Be(1);
        scope.ServiceProvider.GetRequiredService<PriceService>().Calls.Should().Be(1);
    }

    [Fact]
    public void Annotated_method_outside_the_proxied_interface_throws()
    {
        Configure(s => s.AddScoped<ITariffService, TariffService>())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*TariffService.GetExtraAsync: it isn't part of ITariffService*");
    }

    [Fact]
    public void Every_problem_is_listed_in_one_error()
    {
        var error = Configure(s =>
        {
            s.AddKeyedScoped<IPriceService, PriceService>("eu");
            s.AddScoped<ITariffService, TariffService>();
        }).Should().Throw<InvalidOperationException>().Which;

        error.Message.Should().StartWith("These [Cacheable]/[CacheEvict] annotations would never run")
            .And.Contain("IPriceService.GetAsync").And.Contain("TariffService.GetExtraAsync");
    }

    [Fact]
    public void Annotated_service_that_is_not_registered_is_not_reported()
    {
        // It may just be unused by this app (e.g. it comes from a shared library).
        Configure(_ => { }).Should().NotThrow();
    }

    [Fact]
    public void Annotated_controller_is_reported()
    {
        var assembly = EmitAssembly(("ReportController", true), ("ReportService", true), ("HealthController", false));

        var problems = UnreachableAnnotations.InControllers([assembly]).ToList();

        problems.Should().ContainSingle().Which.Should()
            .StartWith("ReportController.Get: controllers are created by ASP.NET Core");
    }

    /// <summary>
    /// Builds classes in a throwaway in-memory assembly, so this test assembly holds no annotated
    /// controller (it would make every other test's AddAnnotationCaching fail).
    /// </summary>
    private static Assembly EmitAssembly(params (string Name, bool Annotated)[] types)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"FakeWebApp{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("FakeWebApp");
        var cacheable = new CustomAttributeBuilder(
            typeof(CacheableAttribute).GetConstructors().Single(), ["report", 60, new[] { "id" }]);

        foreach (var (name, annotated) in types)
        {
            var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class);
            var method = type.DefineMethod("Get", MethodAttributes.Public, typeof(Task<int>), [typeof(int)]);
            method.DefineParameter(1, ParameterAttributes.None, "id");
            if (annotated) method.SetCustomAttribute(cacheable);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            type.CreateType();
        }

        return assembly;
    }
}

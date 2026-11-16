using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAside.Internal.Proxying;

/// <summary>
/// Finds [Cacheable]/[CacheEvict] annotations that no proxy will ever run, so they fail at startup instead
/// of silently doing nothing. Only reports cases that are certain to be unreachable:
/// <list type="bullet">
/// <item>annotated services registered in a way that isn't proxied (keyed, open generic, as a class, factory, instance);</item>
/// <item>annotated methods of a proxied class that aren't part of any interface it's resolved through;</item>
/// <item>annotated controllers, which ASP.NET Core creates itself rather than resolving through an interface.</item>
/// </list>
/// An annotated type that is never registered at all (other than a controller) is not reported: it may
/// simply be unused by this app, e.g. a type from a shared library.
/// </summary>
internal static class UnreachableAnnotations
{
    private const BindingFlags AllMethods =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void ThrowIfAny(IServiceCollection services, IReadOnlyCollection<ProxyRegistration> registrations)
    {
        var problems = new List<string>();
        problems.AddRange(InSkippedRegistrations(services, registrations));
        problems.AddRange(OutsideProxiedInterfaces(services));
        problems.AddRange(InControllers(AssembliesUsingThisLibrary()));

        if (problems.Count > 0) throw CachingErrors.UnreachableAnnotations(problems);
    }

    /// <summary>Registrations with annotations that can't be proxied at all.</summary>
    private static IEnumerable<string> InSkippedRegistrations(IServiceCollection services, IReadOnlyCollection<ProxyRegistration> registrations)
    {
        var proxiedTypes = registrations.Select(r => r.ImplementationType).ToHashSet();

        foreach (var descriptor in services)
        {
            if (CachingProxyRegistrar.IsProxyable(descriptor)) continue; // checked by OutsideProxiedInterfaces

            var serviceType = descriptor.ServiceType;

            // A class registered as itself is fine when it's the target a proxy resolves.
            if (!descriptor.IsKeyedService && !serviceType.IsInterface && proxiedTypes.Contains(serviceType)) continue;

            var annotated = AnnotatedMethods(descriptor);
            if (annotated.Count == 0) continue;

            var reason =
                descriptor.IsKeyedService ? "it is registered as a keyed service, and keyed registrations aren't proxied." :
                serviceType.IsGenericTypeDefinition ? "it is registered as an open generic, and open generic registrations aren't proxied." :
                !serviceType.IsInterface ? $"it is registered as the class {serviceType.Name}, but only interface registrations are proxied. Register it as services.Add{descriptor.Lifetime}<IYourService, {serviceType.Name}>()." :
                descriptor.ImplementationInstance is not null ? "it is registered as an instance, but only registrations with an implementation type are proxied. Register it as services.AddSingleton<Interface, Implementation>()." :
                $"it is registered with a factory, but only registrations with an implementation type are proxied. Register it as services.Add{descriptor.Lifetime}<Interface, Implementation>().";

            foreach (var method in annotated) yield return $"{Describe(method)}: {reason}";
        }
    }

    /// <summary>
    /// Every [Cacheable]/[CacheEvict] method a registration exposes: on its service type, on the interfaces of a
    /// class registered as itself (resolving the class bypasses them), and on its implementation type.
    /// </summary>
    internal static List<MethodInfo> AnnotatedMethods(ServiceDescriptor descriptor)
    {
        var serviceType = descriptor.ServiceType;
        var implementationType = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

        return AnnotatedMethods(serviceType)
            .Concat(serviceType.IsInterface ? [] : serviceType.GetInterfaces().SelectMany(AnnotatedMethods))
            .Concat(implementationType is null || implementationType == serviceType ? [] : AnnotatedMethods(implementationType))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Annotated methods of a class registered behind an interface that no registered interface maps to,
    /// e.g. a public method that isn't declared on the interface.
    /// </summary>
    private static IEnumerable<string> OutsideProxiedInterfaces(IServiceCollection services)
    {
        var covered = new HashSet<(Module, int, Type?)>();
        var interfacesByType = new Dictionary<Type, HashSet<Type>>();
        foreach (var descriptor in services.Where(CachingProxyRegistrar.IsProxyable))
        {
            var (serviceType, implementationType) = (descriptor.ServiceType, descriptor.ImplementationType!);
            if (!interfacesByType.TryGetValue(implementationType, out var interfaces))
                interfacesByType[implementationType] = interfaces = [];
            interfaces.Add(serviceType);

            foreach (var iface in serviceType.GetInterfaces().Prepend(serviceType))
                covered.UnionWith(implementationType.GetInterfaceMap(iface).TargetMethods.Select(Identity));
        }

        foreach (var (type, interfaces) in interfacesByType)
        {
            foreach (var method in AnnotatedMethods(type).Where(m => !covered.Contains(Identity(m))))
            {
                var names = string.Join(", ", interfaces.Select(t => t.Name));
                yield return $"{Describe(method)}: it isn't part of {names} (the interface it's resolved through), " +
                             "so calls to it never go through the proxy. Annotate a method of that interface instead.";
            }
        }
    }

    /// <summary>Annotated methods on ASP.NET Core controllers in <paramref name="assemblies"/>.</summary>
    internal static IEnumerable<string> InControllers(IEnumerable<Assembly> assemblies)
    {
        foreach (var type in assemblies.SelectMany(LoadableTypes).Where(IsController))
        {
            foreach (var method in AnnotatedMethods(type))
            {
                yield return $"{Describe(method)}: controllers are created by ASP.NET Core, not resolved through an interface, " +
                             "so they can't be proxied. Annotate the interface method of the service the controller calls instead.";
            }
        }
    }

    /// <summary>Loaded assemblies that can contain annotations, i.e. that reference this library.</summary>
    private static IEnumerable<Assembly> AssembliesUsingThisLibrary()
    {
        var self = typeof(CacheableAttribute).Assembly;
        var selfName = self.GetName().Name;
        return AppDomain.CurrentDomain.GetAssemblies().Where(a =>
            !a.IsDynamic && a != self && a.GetReferencedAssemblies().Any(r => r.Name == selfName));
    }

    /// <summary>Follows ASP.NET Core's rule: public, non-abstract, non-generic, named *Controller or marked [Controller], not [NonController].</summary>
    private static bool IsController(Type type)
    {
        if (!type.IsClass || !type.IsPublic || type.IsAbstract || type.ContainsGenericParameters) return false;
        if (HasAttribute(type, "Microsoft.AspNetCore.Mvc.NonControllerAttribute")) return false;
        return type.Name.EndsWith("Controller", StringComparison.Ordinal) ||
               HasAttribute(type, "Microsoft.AspNetCore.Mvc.ControllerAttribute");
    }

    // Matched by name so the library doesn't depend on ASP.NET Core.
    private static bool HasAttribute(Type type, string attributeFullName) =>
        type.GetCustomAttributes(inherit: true).Any(a =>
        {
            for (var t = a.GetType(); t != null; t = t.BaseType)
                if (t.FullName == attributeFullName) return true;
            return false;
        });

    private static IEnumerable<MethodInfo> AnnotatedMethods(Type type)
    {
        var methods = type.IsInterface
            ? type.GetInterfaces().Prepend(type).SelectMany(t => t.GetMethods())
            : type.GetMethods(AllMethods);
        return methods.Distinct().Where(m =>
            m.IsDefined(typeof(CacheableAttribute), inherit: false) ||
            m.IsDefined(typeof(CacheEvictAttribute), inherit: false));
    }

    // MethodInfo equality also compares the type it was reflected from; this identifies the method itself.
    private static (Module, int, Type?) Identity(MethodInfo method) => (method.Module, method.MetadataToken, method.DeclaringType);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    internal static string Describe(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";
}

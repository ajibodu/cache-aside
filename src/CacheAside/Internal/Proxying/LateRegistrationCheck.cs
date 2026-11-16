using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CacheAside.Internal.Proxying;

/// <summary>
/// AddAnnotationCaching only proxies services registered before it. This runs when the host starts (in
/// <see cref="StartingAsync"/>, before any hosted service, including the web server) and fails startup if an
/// annotated service was registered after it, instead of leaving that service silently uncached.
/// <para>
/// It looks only at registrations that didn't exist when AddAnnotationCaching ran (compared by reference,
/// so a registration swapped in with services.Replace counts as new even though the count is unchanged).
/// That costs about a millisecond, once. Without a host (a provider built by hand, e.g. in a test) this never runs.
/// </para>
/// </summary>
internal sealed class LateRegistrationCheck(IServiceCollection services) : IHostedLifecycleService
{
    private IServiceCollection? _services = services;
    private HashSet<ServiceDescriptor> _known = [];

    /// <summary>Marks every registration that exists now as handled by AddAnnotationCaching.</summary>
    public void MarkKnown() => _known = new HashSet<ServiceDescriptor>(_services!, ReferenceEqualityComparer.Instance);

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var services = Interlocked.Exchange(ref _services, null); // check once, then let the collection go
        if (services is null) return Task.CompletedTask;

        var known = _known;
        _known = [];
        var late = services
            .Where(d => !known.Contains(d))
            .SelectMany(d => UnreachableAnnotations.AnnotatedMethods(d).Select(m => (d.ServiceType, Method: m)))
            .Select(x => $"{UnreachableAnnotations.Describe(x.Method)}: {x.ServiceType.Name} was registered after " +
                         $"{nameof(CachingServiceCollectionExtensions.AddAnnotationCaching)}, so it isn't cached. " +
                         $"Call {nameof(CachingServiceCollectionExtensions.AddAnnotationCaching)} after registering it.")
            .ToList();

        if (late.Count > 0) throw CachingErrors.UnreachableAnnotations(late);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using CacheAside.Internal.Plans;

namespace CacheAside;

/// <summary>
/// Adds a value from an injected service to the key, for results that depend on something other than the
/// arguments, such as the current user. Goes on the same method as <see cref="CacheableAttribute"/> or
/// <see cref="CacheEvictAttribute"/>, and can be repeated.
/// </summary>
/// <remarks>
/// <para>
/// <typeparamref name="TService"/> is resolved on every call from the scope the cached service came from, so a
/// scoped <c>CurrentUser</c> gives each request its own user. A singleton service can't use a scoped or transient
/// <typeparamref name="TService"/> (it would only ever see the first one); that, an unregistered service and an
/// unknown member all fail at startup.
/// </para>
/// <para>
/// Key layout: <c>prefix:{service values}:{argument values}</c>, with service values sorted by type name then member.
/// If a value is <c>null</c> the call fails, rather than giving everyone without a value one shared entry.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Cacheable("dashboard", 300)]
/// [CacheKeyFrom&lt;CurrentUser&gt;(nameof(CurrentUser.Id))]
/// Task&lt;Dashboard&gt; GetMyDashboardAsync();   // key: dashboard:{user id}
/// </code>
/// </example>
/// <typeparam name="TService">The registered service to read from.</typeparam>
/// <param name="member">The property or field to read. Can be a path, e.g. <c>"Tenant.Code"</c>.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class CacheKeyFromAttribute<TService>(string member) : Attribute, ICacheKeySource
    where TService : notnull
{
    /// <summary>The property or field of <typeparamref name="TService"/> to read, e.g. <c>"Id"</c> or <c>"Tenant.Code"</c>.</summary>
    public string Member { get; } = member;

    Type ICacheKeySource.ServiceType => typeof(TService);
}

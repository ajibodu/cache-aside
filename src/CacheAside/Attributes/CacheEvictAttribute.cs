using CacheAside.Internal.Plans;

namespace CacheAside;

/// <summary>
/// Removes a cached entry after the method completes successfully. If the method throws, nothing is removed.
/// </summary>
/// <remarks>
/// It removes the one key built from this call's values, so to clear what a <see cref="CacheableAttribute"/> stored,
/// use the same prefix, the same cache, and key arguments that produce the same values in the same order.
/// The parameter names don't have to match.
/// </remarks>
/// <example>
/// <code>
/// [CacheEvict("account", nameof(id))]
/// Task UpdateAccountAsync(string id, AccountUpdate update);   // removes account:{id}
/// </code>
/// </example>
/// <param name="keyPrefix">First part of the key, e.g. <c>"account"</c>.</param>
/// <param name="keyArguments">The parameters whose values make up the rest of the key, in order.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class CacheEvictAttribute(string keyPrefix, params string[] keyArguments)
    : Attribute, ICacheAnnotation
{
    /// <summary>First part of the key.</summary>
    public string KeyPrefix { get; } = keyPrefix;

    /// <summary>The parameters (or parameter property paths) whose values make up the rest of the key.</summary>
    public string[] KeyArguments { get; } = keyArguments;

    /// <summary>The named cache to use, added with <see cref="CachingOptions.AddCache"/>. Leave unset for the default cache.</summary>
    public string? Cache { get; set; }
}

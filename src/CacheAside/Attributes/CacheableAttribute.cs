using CacheAside.Internal.Plans;

namespace CacheAside;

/// <summary>
/// Caches what an async method returns. On a hit the method isn't called; on a miss it runs and its result is stored.
/// </summary>
/// <example>
/// <code>
/// [Cacheable("account", 300, nameof(accountId))]
/// Task&lt;Account&gt; GetAccountAsync(string accountId);   // key: account:{accountId}
/// </code>
/// </example>
/// <param name="keyPrefix">First part of the key, e.g. <c>"account"</c>.</param>
/// <param name="durationSeconds">How long the result is kept. 0 or less means it doesn't expire.</param>
/// <param name="keyArguments">
/// The parameters whose values make up the rest of the key, in order. A dotted path reads a property of a
/// parameter, e.g. <c>"request.Account.Id"</c>.
/// </param>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class CacheableAttribute(string keyPrefix, int durationSeconds, params string[] keyArguments)
    : Attribute, ICacheAnnotation
{
    /// <summary>First part of the key.</summary>
    public string KeyPrefix { get; } = keyPrefix;

    /// <summary>How long the result is kept, in seconds. 0 or less means it doesn't expire.</summary>
    public int DurationSeconds { get; } = durationSeconds;

    /// <summary>The parameters (or parameter property paths) whose values make up the rest of the key.</summary>
    public string[] KeyArguments { get; } = keyArguments;

    /// <summary>The named cache to use, added with <see cref="CachingOptions.AddCache"/>. Leave unset for the default cache.</summary>
    public string? Cache { get; set; }
}

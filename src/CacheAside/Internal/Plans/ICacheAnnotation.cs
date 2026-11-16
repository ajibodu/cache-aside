namespace CacheAside.Internal.Plans;

/// <summary>What [Cacheable] and [CacheEvict] have in common: everything needed to build a key and pick a cache.</summary>
internal interface ICacheAnnotation
{
    string KeyPrefix { get; }
    string[] KeyArguments { get; }
    string? Cache { get; }
}

/// <summary>A [CacheKeyFrom&lt;T&gt;]: a key value read from an injected service.</summary>
internal interface ICacheKeySource
{
    Type ServiceType { get; }
    string Member { get; }
}

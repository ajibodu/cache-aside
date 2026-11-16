using StackExchange.Redis;

namespace CacheAside.IntegrationTest;

/// <summary>
/// A test that needs Redis. Uses CACHEASIDE_TEST_REDIS (default localhost:6379) and is skipped when Redis
/// isn't reachable, unless CACHEASIDE_REQUIRE_REDIS=true (set on CI) makes it run and fail instead.
/// Locally: docker run -d -p 6379:6379 redis
/// </summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CACHEASIDE_TEST_REDIS") ?? "localhost:6379";

    private static readonly bool Required =
        string.Equals(Environment.GetEnvironmentVariable("CACHEASIDE_REQUIRE_REDIS"), "true", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var redis = ConnectionMultiplexer.Connect($"{ConnectionString},connectTimeout=1000,abortConnect=false");
            return redis.IsConnected;
        }
        catch
        {
            return false;
        }
    });

    public RedisFactAttribute()
    {
        if (!Required && !Available.Value) Skip = $"Redis not reachable at {ConnectionString}";
    }
}

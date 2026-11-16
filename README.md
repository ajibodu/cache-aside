# CacheAside

Cache what your service methods return by adding an attribute, using whatever cache your app already has.

```csharp
public interface IProductService
{
    [Cacheable("product", 300, nameof(id))]
    Task<Product?> GetAsync(int id);

    [CacheEvict("product", nameof(id))]
    Task UpdateAsync(int id, ProductUpdate update);
}
```

The first `GetAsync(1)` runs your method and stores the result under `product:1`. Later calls are answered from the cache until the entry expires or `UpdateAsync(1, ...)` succeeds.

## Install

```shell
dotnet add package CacheAside
```

Supports .NET 8 and .NET 10.

## Setup

Register your services, then call `AddAnnotationCaching` **last** and tell it how to talk to your cache:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");
builder.Services.AddScoped<IProductService, ProductService>();

// Last: it wraps the services registered above.
builder.Services.AddAnnotationCaching(o => o.UseCache<IDistributedCache>(
    get: (cache, key) => cache.GetAsync(key),
    set: (cache, key, value, ttl) => cache.SetAsync(key, value, new() { AbsoluteExpirationRelativeToNow = ttl }),
    remove: (cache, key) => cache.RemoveAsync(key),
    keyPrefix: "myapp:"));
```

A few things to know:

- Only services registered against an interface are cached (`AddScoped<IProductService, ProductService>()`), and only calls made through that interface. A method calling another method on the same class skips the cache.
- If a cached service is registered after `AddAnnotationCaching`, the app refuses to start and names the service.
- The attributes can go on the interface or on the implementing class.

## Plugging in a cache

CacheAside doesn't include a cache. You connect yours in one of three ways:

| Method | Use it for | Behaviour |
|---|---|---|
| `UseCache(get, set, remove)` | Most caches: `IDistributedCache`, Redis, `IMemoryCache` | Values are stored as UTF-8 JSON. `null` results aren't cached. If several callers miss at once, each runs the method. |
| `UseCache(getOrCreate, remove)` | Caches that handle misses themselves, such as `HybridCache` | Concurrent misses for the same key run the method once. |
| `UseCacheService<T>()` | Full control, or typed values without JSON | Implement `ICacheService`. `T` comes from DI, or is created and disposed for you. |

HybridCache, for example:

```csharp
builder.Services.AddHybridCache();
builder.Services.AddAnnotationCaching(o => o.UseCache<HybridCache>(
    getOrCreate: (cache, key, factory, ttl) => cache.GetOrCreateAsync(
        key, factory, static async (f, _) => await f(), new HybridCacheEntryOptions { Expiration = ttl }).AsTask(),
    remove: (cache, key) => cache.RemoveAsync(key).AsTask()));
```

If you don't configure a cache at all, the `ICacheService` registered in DI is used.

### More than one cache

Name extra caches and pick one per method:

```csharp
builder.Services.AddAnnotationCaching(o =>
{
    o.UseCache<IMemoryCache>(...);                                         // the default
    o.AddCache("shared", c => c.UseCache<IDistributedCache>(..., keyPrefix: "myapp:"));
});

[Cacheable("rates", 60, nameof(currency), Cache = "shared")]
Task<Rate> GetRateAsync(string currency);
```

A misspelled cache name fails at startup and lists the caches you do have.

## Cache keys

A key is the cache's `keyPrefix`, the attribute's prefix, then the values of the key arguments in the order you list them: `myapp:product:42`.

- Key arguments are parameter names. Use `nameof(id)` so a rename can't silently break them.
- Use a dotted path to read a property: `[Cacheable("quote", 60, "request.Account.Id", "request.Currency")]`. Write paths as strings, because `nameof(request.Account.Id)` gives just `"Id"`.
- Strings, numbers, enums, `Guid` and dates are written as plain text; `null` becomes `null`; anything else is serialized to JSON.
- With no key arguments, every call shares one entry.

### Values that aren't arguments

When a result depends on something injected, such as the current user, add it to the key with `[CacheKeyFrom<T>]`. Otherwise one user's data would be served to everyone:

```csharp
[Cacheable("dashboard", 300)]
[CacheKeyFrom<CurrentUser>(nameof(CurrentUser.Id))]
Task<Dashboard> GetMyDashboardAsync();            // dashboard:{user id}
```

`CurrentUser` is resolved on every call from the request's scope, so register the cached service as **scoped**. A singleton would only ever see the first user, so that combination fails at startup. If the value is `null` the call fails rather than sharing one entry between everyone without a value.

### Eviction

`[CacheEvict]` removes one key, built from its own call, after the method succeeds. It clears what `[Cacheable]` stored when the prefix, the cache and the values (in order) are the same. The parameter names don't have to match:

```csharp
[Cacheable("account", 300, nameof(accountId))] Task<Account> GetAsync(string accountId);
[CacheEvict("account", nameof(id))]            Task UpdateAsync(string id, AccountUpdate update);
```

`UpdateAsync("A1", ...)` removes `account:A1` and nothing else.

## Mistakes caught at startup

`AddAnnotationCaching` checks everything up front, so these fail when the app starts instead of on the first call:

- a key argument or property path that doesn't exist
- `[Cacheable]` on a method that doesn't return `Task<T>`, or `[CacheEvict]` on one that doesn't return `Task`/`Task<T>`
- generic methods, `ref`/`out` parameters, or both attributes on one method
- an unknown cache name, or a named cache with no `UseCache...`
- an attribute that could never run: on a controller, on a service registered with a factory, an instance, a key or as its own class, or on a method that isn't part of the interface
- `[CacheKeyFrom<T>]` where `T` isn't registered, has no such member, or is scoped while the cached service is a singleton
- a cached service registered after `AddAnnotationCaching` (checked when the host starts)

## Limitations

- Only `Task` and `Task<T>` methods. `ValueTask` and synchronous methods aren't supported.
- Proxies are generated at runtime with Castle DynamicProxy, so Native AOT isn't supported.
- With the `UseCache` delegates, return types must round-trip through System.Text.Json.
- Stampede protection, size limits and eviction policy come from your cache, not from this library.

## How it works

At startup, `AddAnnotationCaching` reads the attributes, checks them, and works out each method's key layout once. It then swaps each annotated registration for a Castle DynamicProxy that intercepts only the annotated methods. On a call, the proxy builds the key and asks your cache; on a miss it calls the real method through a compiled delegate and stores the result.

## Development

```shell
dotnet test test/CacheAside.Test                 # unit tests

docker run -d -p 6379:6379 redis                 # integration tests use a real Redis
dotnet test test/CacheAside.IntegrationTest
```

Redis tests are skipped when Redis isn't reachable. Point them elsewhere with `CACHEASIDE_TEST_REDIS`; CI sets `CACHEASIDE_REQUIRE_REDIS=true` so they fail instead of being skipped. `sample/CacheAside.SampleApi` is a small API you can run and poke at.

To release, bump `<Version>` in `src/CacheAside/CacheAside.csproj`. Merging to `main` runs the tests and publishes to NuGet.

## License

MIT

using CacheAside;
using CacheAside.SampleApi;
using Microsoft.Extensions.Caching.Distributed;

var builder = WebApplication.CreateBuilder(args);

// Any cache works. This uses the in-memory IDistributedCache; swap in AddStackExchangeRedisCache(...) for Redis.
builder.Services.AddDistributedMemoryCache();

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IProductService, ProductService>();

// Keep this last: it wraps the services registered above.
builder.Services.AddAnnotationCaching(o => o.UseCache<IDistributedCache>(
    get: (cache, key) => cache.GetAsync(key),
    set: (cache, key, value, ttl) => cache.SetAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }),
    remove: (cache, key) => cache.RemoveAsync(key),
    keyPrefix: "sample:"));

var app = builder.Build();

// Stand-in for authentication: the caller's id comes from a header.
app.Use((context, next) =>
{
    context.RequestServices.GetRequiredService<CurrentUser>().Id = context.Request.Headers["X-User"].FirstOrDefault();
    return next(context);
});

app.MapGet("/products/{id:int}", (int id, IProductService products) => products.GetAsync(id));
app.MapPut("/products/{id:int}", (int id, ProductUpdate update, IProductService products) => products.UpdateAsync(id, update));
app.MapGet("/me/favourites", (IProductService products) => products.GetMyFavouritesAsync());

app.Run();

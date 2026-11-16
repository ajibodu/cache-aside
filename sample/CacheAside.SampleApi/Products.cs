namespace CacheAside.SampleApi;

public record Product(int Id, string Name, decimal Price);

public record ProductUpdate(string Name, decimal Price);

public interface IProductService
{
    [Cacheable("product", 300, nameof(id))]
    Task<Product?> GetAsync(int id);

    // Clears product:{id} once the update succeeds, so the next read sees the new price.
    [CacheEvict("product", nameof(id))]
    Task<Product> UpdateAsync(int id, ProductUpdate update);

    // One entry per caller: the user id comes from the scoped CurrentUser, not from the arguments.
    [Cacheable("favourites", 60)]
    [CacheKeyFrom<CurrentUser>(nameof(CurrentUser.Id))]
    Task<IReadOnlyList<Product>> GetMyFavouritesAsync();
}

public class ProductService(CurrentUser user, ILogger<ProductService> logger) : IProductService
{
    private static readonly Dictionary<int, Product> Products = new()
    {
        [1] = new(1, "Keyboard", 49.99m),
        [2] = new(2, "Mouse", 19.99m),
    };

    public async Task<Product?> GetAsync(int id)
    {
        logger.LogInformation("Cache miss: loading product {Id}", id);
        await Task.Delay(500); // a slow database
        return Products.GetValueOrDefault(id);
    }

    public Task<Product> UpdateAsync(int id, ProductUpdate update)
    {
        var product = new Product(id, update.Name, update.Price);
        Products[id] = product;
        return Task.FromResult(product);
    }

    public async Task<IReadOnlyList<Product>> GetMyFavouritesAsync()
    {
        logger.LogInformation("Cache miss: loading favourites for {User}", user.Id);
        await Task.Delay(500);
        return [Products[user.Id == "alice" ? 1 : 2]];
    }
}

/// <summary>The signed-in user, filled once per request.</summary>
public class CurrentUser
{
    public string? Id { get; set; }
}

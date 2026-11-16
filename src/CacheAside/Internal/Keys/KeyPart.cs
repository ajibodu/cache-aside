namespace CacheAside.Internal.Keys;

/// <summary>
/// One segment of a cache key: the argument at <paramref name="position"/>, optionally projected
/// through a compiled, null-safe property path (e.g. request.Account.Id).
/// </summary>
internal readonly struct KeyPart(int position, Func<object?, object?>? accessor)
{
    public object? GetValue(object?[] args)
    {
        var value = args[position];
        return accessor == null ? value : accessor(value);
    }
}

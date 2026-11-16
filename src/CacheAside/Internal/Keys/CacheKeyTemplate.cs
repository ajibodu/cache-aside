using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CacheAside.Internal.Keys;

/// <summary>
/// Builds the cache key for one annotated method: <c>prefix:serviceValue1...:argValue1:argValue2...</c>.
/// The prefix already includes the cache's keyPrefix; it was joined at startup.
/// </summary>
internal sealed class CacheKeyTemplate(string prefix, ServiceKeyPart[] serviceParts, KeyPart[] parts)
{
    /// <summary>The services this key reads from ([CacheKeyFrom&lt;T&gt;]), checked against registrations at startup.</summary>
    public IEnumerable<Type> ServiceTypes => serviceParts.Select(p => p.ServiceType);

    public string Build(object?[] args, IServiceProvider services)
    {
        if (serviceParts.Length > 0)
        {
            var sb = new StringBuilder(prefix);
            foreach (var part in serviceParts) sb.Append(':').Append(Format(part.GetValue(services)));
            foreach (var part in parts) sb.Append(':').Append(Format(part.GetValue(args)));
            return sb.ToString();
        }

        switch (parts.Length)
        {
            case 0:
                return prefix;
            case 1:
                return string.Concat(prefix, ":", Format(parts[0].GetValue(args)));
            default:
                var sb = new StringBuilder(prefix);
                foreach (var part in parts)
                {
                    sb.Append(':').Append(Format(part.GetValue(args)));
                }
                return sb.ToString();
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        Enum e => e.ToString(),
        IFormattable f when IsScalar(value.GetType()) => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, value.GetType()),
    };

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
        type == typeof(DateOnly) || type == typeof(TimeOnly);
}

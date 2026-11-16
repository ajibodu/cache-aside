using System.Linq.Expressions;
using System.Reflection;
using CacheAside.Internal.Plans;

namespace CacheAside.Internal.Keys;

/// <summary>
/// Turns key arguments ("accountId", "request.Account.Id") into <see cref="KeyPart"/>s at startup:
/// the first segment becomes a parameter position, any further segments a compiled property accessor.
/// Every segment is validated against declared types, so typos fail at startup.
/// </summary>
internal static class KeyPartResolver
{
    public static KeyPart[] Resolve(string method, MethodInfo declaredOn, string[] keyArguments)
    {
        var parameters = declaredOn.GetParameters();
        var parts = new KeyPart[keyArguments.Length];
        for (var i = 0; i < keyArguments.Length; i++)
        {
            var segments = keyArguments[i].Split('.');
            var position = Array.FindIndex(parameters, p => p.Name == segments[0]);
            if (position < 0)
                throw CachingErrors.InvalidAnnotation(method, $"key argument '{keyArguments[i]}': '{segments[0]}' does not match any parameter.");

            var accessor = segments.Length == 1
                ? null
                : BuildMemberPathAccessor(method, keyArguments[i], parameters[position].ParameterType, segments.AsSpan(1));
            parts[i] = new KeyPart(position, accessor);
        }
        return parts;
    }

    /// <summary>
    /// Turns [CacheKeyFrom&lt;T&gt;] attributes into <see cref="ServiceKeyPart"/>s, in a fixed order (service type
    /// name, then member) so that a [Cacheable] and a [CacheEvict] build the same key whatever order the
    /// attributes are written in.
    /// </summary>
    public static ServiceKeyPart[] ResolveServices(string method, IEnumerable<ICacheKeySource> sources) =>
        sources
            .OrderBy(s => s.ServiceType.FullName, StringComparer.Ordinal)
            .ThenBy(s => s.Member, StringComparer.Ordinal)
            .Select(s =>
            {
                var description = $"[CacheKeyFrom<{s.ServiceType.Name}>(\"{s.Member}\")]";
                if (string.IsNullOrWhiteSpace(s.Member))
                    throw CachingErrors.InvalidAnnotation(method, $"{description}: the member is empty.");
                var accessor = BuildMemberPathAccessor(method, description, s.ServiceType, s.Member.Split('.'));
                return new ServiceKeyPart(s.ServiceType, accessor, method, description);
            })
            .ToArray();

    /// <summary>Compiles value => value?.A?.B?.C (boxed), returning null if any link in the chain is null.</summary>
    private static Func<object?, object?> BuildMemberPathAccessor(string method, string keyArgument, Type rootType, ReadOnlySpan<string> members)
    {
        var input = Expression.Parameter(typeof(object), "value");
        var returnNull = Expression.Label(typeof(object), "returnNull");
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();

        Expression current = Expression.Convert(input, rootType);
        var currentType = rootType;

        foreach (var member in members)
        {
            // Hold the current link in a variable and bail out with null if it is null.
            var link = Expression.Variable(currentType);
            variables.Add(link);
            body.Add(Expression.Assign(link, current));

            Expression instance = link;
            if (Nullable.GetUnderlyingType(currentType) is { } underlying)
            {
                body.Add(Expression.IfThen(Expression.Not(Expression.Property(link, "HasValue")), Expression.Return(returnNull, Expression.Constant(null))));
                instance = Expression.Property(link, "Value");
                currentType = underlying;
            }
            else if (!currentType.IsValueType)
            {
                body.Add(Expression.IfThen(Expression.Equal(link, Expression.Constant(null, currentType)), Expression.Return(returnNull, Expression.Constant(null))));
            }

            var memberInfo = FindMember(currentType, member)
                ?? throw CachingErrors.InvalidAnnotation(method, $"key argument '{keyArgument}': type '{currentType.Name}' has no public property or field '{member}'.");

            current = Expression.MakeMemberAccess(instance, memberInfo);
            currentType = memberInfo is PropertyInfo p ? p.PropertyType : ((FieldInfo)memberInfo).FieldType;
        }

        body.Add(Expression.Label(returnNull, Expression.Convert(current, typeof(object))));
        return Expression.Lambda<Func<object?, object?>>(Expression.Block(variables, body), input).Compile();
    }

    private static MemberInfo? FindMember(Type type, string member)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        // Interfaces don't surface inherited members, so search their base interfaces too.
        foreach (var candidate in type.IsInterface ? type.GetInterfaces().Prepend(type) : [type])
        {
            var property = candidate.GetProperties(flags)
                .Where(p => p.Name == member && p.GetIndexParameters().Length == 0 && p.CanRead)
                .OrderByDescending(p => InheritanceDepth(p.DeclaringType)) // most-derived wins when hidden with 'new'
                .FirstOrDefault();
            if (property != null) return property;

            var field = candidate.GetField(member, flags);
            if (field != null) return field;
        }
        return null;
    }

    private static int InheritanceDepth(Type? type)
    {
        var depth = 0;
        for (; type != null; type = type.BaseType) depth++;
        return depth;
    }
}

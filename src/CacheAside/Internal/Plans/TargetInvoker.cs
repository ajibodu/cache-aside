using System.Linq.Expressions;
using System.Reflection;

namespace CacheAside.Internal.Plans;

/// <summary>Compiles a direct, reflection-free call to the real method, used on a cache miss.</summary>
internal static class TargetInvoker
{
    /// <summary>Compiles (target, args) => ((TService)target).Method((T0)args[0], ...).</summary>
    public static Func<object, object?[], Task<T>> Compile<T>(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var args = Expression.Parameter(typeof(object?[]), "args");
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            method.GetParameters().Select((p, i) =>
                Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), p.ParameterType)));
        return Expression.Lambda<Func<object, object?[], Task<T>>>(call, target, args).Compile();
    }
}

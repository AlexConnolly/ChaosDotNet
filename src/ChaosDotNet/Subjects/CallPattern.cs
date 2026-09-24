using System.Linq.Expressions;
using System.Reflection;

namespace ChaosDotNet;

/// <summary>Argument matchers for <c>ChaosSubject&lt;T&gt;</c> setups and filters. Use them only inside expressions.</summary>
public static class Arg
{
    /// <summary>Matches any value.</summary>
    public static T Any<T>() => default!;

    /// <summary>Matches values for which <paramref name="predicate"/> returns <see langword="true"/>.</summary>
    public static T Is<T>(Func<T, bool> predicate) => default!;
}

/// <summary>A method call written as an expression, for example <c>g => g.ChargeAsync(Arg.Any&lt;string&gt;(), 10m)</c>.</summary>
internal sealed class CallPattern
{
    private readonly Func<object?, bool>[] _arguments;

    private CallPattern(MethodInfo method, Func<object?, bool>[] arguments, string text)
    {
        Method = method;
        _arguments = arguments;
        Text = text;
    }

    public MethodInfo Method { get; }

    public string Text { get; }

    public static CallPattern From(LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            body = convert.Operand;
        }

        return body switch
        {
            MethodCallExpression call when call.Object is ParameterExpression =>
                new CallPattern(call.Method, call.Arguments.Select(Matcher).ToArray(), body.ToString()),
            MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression } =>
                new CallPattern(property.GetMethod ?? throw new ArgumentException($"{property.Name} has no getter.", nameof(expression)), [], body.ToString()),
            _ => throw new ArgumentException($"'{expression}' must call a method or read a property on the subject, for example g => g.GetAsync(Arg.Any<int>()).", nameof(expression)),
        };
    }

    public bool Matches(MethodInfo method, IReadOnlyList<object?> arguments)
    {
        if (!SameMethod(method))
        {
            return false;
        }

        for (var i = 0; i < _arguments.Length; i++)
        {
            if (!_arguments[i](arguments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool SameMethod(MethodInfo method)
    {
        if (method == Method)
        {
            return true;
        }

        return method.IsGenericMethod
            && Method.IsGenericMethod
            && method.GetGenericMethodDefinition() == Method.GetGenericMethodDefinition()
            && method.GetGenericArguments().SequenceEqual(Method.GetGenericArguments());
    }

    private static Func<object?, bool> Matcher(Expression argument)
    {
        var inner = argument is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : argument;
        if (inner is MethodCallExpression call && call.Method.DeclaringType == typeof(Arg))
        {
            if (call.Method.Name == nameof(Arg.Any))
            {
                return _ => true;
            }

            var predicate = Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke()
                ?? throw new ArgumentException("Arg.Is needs a predicate.", nameof(argument));
            var method = predicate.GetType().GetMethod("Invoke")!;
            var type = call.Method.GetGenericArguments()[0];
            return value => (value is null ? !type.IsValueType || Nullable.GetUnderlyingType(type) is not null : type.IsInstanceOfType(value))
                && (bool)method.Invoke(predicate, [value])!;
        }

        var expected = Expression.Lambda<Func<object?>>(Expression.Convert(argument, typeof(object))).Compile()();
        return value => Same(value, expected);
    }

    /// <summary>Equal values, or collections (not strings) with equal items in the same order.</summary>
    private static bool Same(object? actual, object? expected)
    {
        if (Equals(actual, expected))
        {
            return true;
        }

        return actual is System.Collections.IEnumerable a and not string
            && expected is System.Collections.IEnumerable e and not string
            && a.Cast<object?>().SequenceEqual(e.Cast<object?>(), EqualityComparer<object?>.Create((x, y) => Same(x, y), x => 0));
    }
}

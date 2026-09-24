using System.Collections;
using System.Reflection;

namespace ChaosDotNet;

/// <summary>
/// Builds values that are valid for a type but unexpected: <see langword="null"/>, empty, boundary and undefined values.
/// Used by the <c>ReturnOddValues()</c> fault, and useful on its own.
/// </summary>
public static class OddValues
{
    private static readonly string[] Strings =
    [
        string.Empty,
        "   ",
        new('x', 10_000),
        "\U0001F47E \u0645\u0631\u062D\u0628\u0627 \u202Eevil\u202C",
        "null",
        "0",
    ];

    private static readonly MethodInfo FromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!;

    /// <summary>Picks an odd value for <paramref name="type"/>. The same <paramref name="random"/> state gives the same value.</summary>
    public static object? For(Type type, Random random)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(random);

        if (TaskResultType(type) is { } resultType)
        {
            return Wrap(type, For(resultType, random));
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return random.Next(2) == 0 ? null : For(underlying, random);
        }

        if (type == typeof(string))
        {
            var pick = random.Next(Strings.Length + 1);
            return pick == Strings.Length ? null : Strings[pick];
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Select(Convert.ToInt64).DefaultIfEmpty(0).ToArray();
            return Enum.ToObject(type, values.Max() + 1 + random.Next(1000));
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => random.Next(2) == 0,
            TypeCode.Byte => Pick(random, byte.MinValue, byte.MaxValue),
            TypeCode.SByte => Pick(random, (sbyte)0, (sbyte)-1, sbyte.MinValue, sbyte.MaxValue),
            TypeCode.Int16 => Pick(random, (short)0, (short)-1, short.MinValue, short.MaxValue),
            TypeCode.UInt16 => Pick(random, ushort.MinValue, ushort.MaxValue),
            TypeCode.Int32 => Pick(random, 0, -1, int.MinValue, int.MaxValue),
            TypeCode.UInt32 => Pick(random, uint.MinValue, uint.MaxValue),
            TypeCode.Int64 => Pick(random, 0L, -1L, long.MinValue, long.MaxValue),
            TypeCode.UInt64 => Pick(random, ulong.MinValue, ulong.MaxValue),
            TypeCode.Single => Pick(random, 0f, -1f, float.MinValue, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity),
            TypeCode.Double => Pick(random, 0d, -1d, double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity),
            TypeCode.Decimal => Pick(random, 0m, -1m, decimal.MinValue, decimal.MaxValue),
            TypeCode.Char => Pick(random, '\0', '\uFFFF', '\u202E'),
            TypeCode.DateTime => Pick(random, DateTime.MinValue, DateTime.MaxValue, DateTime.UnixEpoch, new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _ => Other(type, random),
        };
    }

    internal static Type? TaskResultType(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0]
            : null;

    /// <summary>Wraps a value in the task type a method returns, or returns it as it is.</summary>
    internal static object? Wrap(Type returnType, object? value)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return FromResult.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [value]);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return returnType.GetConstructor([returnType.GetGenericArguments()[0]])!.Invoke([value]);
        }

        return value;
    }

    /// <summary>The value a loose subject returns when no setup matches: completed tasks, empty strings and collections, <see langword="default"/>.</summary>
    internal static object? Default(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type == typeof(ValueTask))
        {
            return default(ValueTask);
        }

        if (TaskResultType(type) is { } resultType)
        {
            return Wrap(type, Default(resultType));
        }

        if (type == typeof(string))
        {
            return string.Empty;
        }

        return Empty(type) ?? (type.IsValueType ? Activator.CreateInstance(type) : null);
    }

    private static object? Other(Type type, Random random)
    {
        if (type == typeof(Guid))
        {
            return Guid.Empty;
        }

        if (type == typeof(DateTimeOffset))
        {
            return Pick(random, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.UnixEpoch, new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        if (type == typeof(TimeSpan))
        {
            return Pick(random, TimeSpan.Zero, TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.FromTicks(-1));
        }

        if (Empty(type) is { } empty)
        {
            var element = ElementType(type);
            if (element is null || random.Next(2) == 0)
            {
                return empty;
            }

            if (type.IsArray)
            {
                return Single(element, random);
            }

            if (empty is IList list)
            {
                list.Add(For(element, random));
            }

            return empty;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static Array Single(Type element, Random random)
    {
        var array = Array.CreateInstance(element, 1);
        array.SetValue(For(element, random), 0);
        return array;
    }

    private static object? Empty(Type type)
    {
        if (type.IsArray)
        {
            return Array.CreateInstance(type.GetElementType()!, 0);
        }

        if (!type.IsGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (arguments.Length == 1 && (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(List<>)))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments));
        }

        if (arguments.Length == 1 && (definition == typeof(ISet<>) || definition == typeof(HashSet<>) || definition == typeof(IReadOnlySet<>)))
        {
            return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(arguments));
        }

        if (arguments.Length == 2 && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(Dictionary<,>)))
        {
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
        }

        return null;
    }

    private static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericArguments().Length == 1 ? type.GetGenericArguments()[0] : null;

    private static object Pick<T>(Random random, params T[] values)
        where T : notnull => values[random.Next(values.Length)];
}

using System.Reflection;
using ChaosDotNet.Factories;
using ChaosDotNet.Proxies;

namespace ChaosDotNet;

/// <summary>A fault that makes interface methods return odd values from <see cref="OddValues"/> instead of calling the target.</summary>
public sealed class OddValueFault : Fault
{
    private readonly Random _random;

    /// <summary>Creates the fault.</summary>
    public OddValueFault(Random random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    /// <inheritdoc />
    public override string Name => "ReturnOddValues";

    internal static MonkeyFault Monkey { get; } = new("ReturnOddValues", MonkeyFaultKind.Weird, random => new OddValueFault(new Random(random.Next())), 2);

    /// <summary>Applies to methods that return a value: not <see langword="void"/>, <see cref="Task"/> or <see cref="ValueTask"/>.</summary>
    public override bool AppliesTo(ChaosCall call) =>
        call is ProxyChaosCall proxy && proxy.Method.ReturnType is var type && type != typeof(void) && type != typeof(Task) && type != typeof(ValueTask);

    /// <summary>The odd value for a return type.</summary>
    public object? NextValue(Type returnType)
    {
        lock (_random)
        {
            return OddValues.For(returnType, _random);
        }
    }

    internal static object? Apply(Fault fault, MethodInfo method, object?[] args) =>
        fault is OddValueFault odd
            ? odd.NextValue(method.ReturnType)
            : throw new NotSupportedException($"The fault '{fault.Name}' is not supported by this veneer.");
}

/// <summary><c>ReturnOddValues()</c> for <see cref="ChaosSubject{T}"/> and <see cref="ProxyFactory{T}"/> windows.</summary>
public static class OddValueFaults
{
    /// <summary>
    /// Methods that return a value get something valid but unexpected instead: <see langword="null"/>, empty strings and
    /// collections, <c>NaN</c>, <c>MinValue</c>, undefined enum values and more. Methods that return nothing pass through.
    /// </summary>
    public static ChaosSubject<T> ReturnOddValues<T>(this WindowBuilder<ChaosSubject<T>, SubjectCall> window)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new OddValueFault(new Random(window.Factory.Seed)));
    }

    /// <summary>
    /// Methods that return a value get something valid but unexpected instead of the real object's answer.
    /// Methods that return nothing pass through.
    /// </summary>
    public static ProxyFactory<T> ReturnOddValues<T>(this WindowBuilder<ProxyFactory<T>, ProxyChaosCall> window)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new OddValueFault(new Random(window.Factory.Seed)));
    }
}

using System.Reflection;

namespace ChaosDotNet.Tests;

public class FakeProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _handler = null!;

    public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args ?? []);
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ChaosDotNet.Proxies;

/// <summary>Controls how <see cref="ChaosProxy.Create{T}"/> builds a veneer over an interface.</summary>
public sealed class ChaosProxyOptions
{
    /// <summary>Which methods go through the timeline. Others pass straight to the target. Defaults to all methods.</summary>
    public Func<MethodInfo, bool>? Intercept { get; init; }

    /// <summary>Builds the call passed to the timeline. Defaults to a <see cref="ProxyChaosCall"/>.</summary>
    public Func<MethodInfo, object?[], ChaosCall>? CreateCall { get; init; }

    /// <summary>Replaces a method's result, for example to wrap a returned client in its own veneer.</summary>
    public Func<MethodInfo, object?, object?>? WrapResult { get; init; }

    /// <summary>Applies a fault that the engine hands back, and returns the method's result instead of calling the target.</summary>
    public Func<Fault, MethodInfo, object?[], object?>? ApplyFault { get; init; }
}

/// <summary>Builds veneers over any interface with <see cref="DispatchProxy"/>.</summary>
public static class ChaosProxy
{
    /// <summary>Creates a veneer over <paramref name="target"/> that follows <paramref name="engine"/>'s timeline.</summary>
    public static T Create<T>(T target, ChaosEngine engine, ChaosProxyOptions? options = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(engine);
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException($"{typeof(T).Name} is not an interface. Veneers over classes need a factory for that client.", nameof(T));
        }

        var proxy = DispatchProxy.Create<T, ChaosDispatchProxy>();
        ((ChaosDispatchProxy)(object)proxy).Initialise(target, engine, options ?? new ChaosProxyOptions());
        return proxy;
    }
}

/// <summary>A call made through a <see cref="ChaosProxy"/> veneer.</summary>
public class ProxyChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    public ProxyChaosCall(MethodInfo method, object?[] arguments)
        : base(OperationName(method))
    {
        Method = method;
        Arguments = arguments;
    }

    /// <summary>The interface method called.</summary>
    public MethodInfo Method { get; }

    /// <summary>The arguments passed.</summary>
    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>The method name without a trailing <c>Async</c>.</summary>
    public static string OperationName(MethodInfo method) =>
        method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name;
}

/// <summary>The <see cref="DispatchProxy"/> behind <see cref="ChaosProxy"/>. Not for direct use.</summary>
public class ChaosDispatchProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> TaskInvokers = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> ValueTaskInvokers = new();

    private object _target = null!;
    private ChaosEngine _engine = null!;
    private ChaosProxyOptions _options = null!;

    internal void Initialise(object target, ChaosEngine engine, ChaosProxyOptions options)
    {
        _target = target;
        _engine = engine;
        _options = options;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (_options.Intercept is not null && !_options.Intercept(targetMethod))
        {
            return Wrap(targetMethod, InvokeTarget(targetMethod, args));
        }

        var call = _options.CreateCall?.Invoke(targetMethod, args) ?? new ProxyChaosCall(targetMethod, args);
        var returnType = targetMethod.ReturnType;

        if (returnType == typeof(Task))
        {
            return InvokeTaskAsync(targetMethod, args, call);
        }

        if (returnType == typeof(ValueTask))
        {
            return InvokeValueTaskAsync(targetMethod, args, call);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var invoker = TaskInvokers.GetOrAdd(returnType.GetGenericArguments()[0], static t =>
                typeof(ChaosDispatchProxy).GetMethod(nameof(InvokeTaskOfTAsync), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(t));
            return invoker.Invoke(this, [targetMethod, args, call]);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var invoker = ValueTaskInvokers.GetOrAdd(returnType.GetGenericArguments()[0], static t =>
                typeof(ChaosDispatchProxy).GetMethod(nameof(InvokeValueTaskOfTAsync), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(t));
            return invoker.Invoke(this, [targetMethod, args, call]);
        }

        var fault = _engine.BeforeCall(call);
        if (fault is not null)
        {
            return ApplyFault(fault, targetMethod, args);
        }

        try
        {
            var result = InvokeTarget(targetMethod, args);
            _engine.CallSucceeded(call);
            return Wrap(targetMethod, result);
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private async Task InvokeTaskAsync(MethodInfo method, object?[] args, ChaosCall call)
    {
        var fault = await _engine.BeforeCallAsync(call, FindToken(args)).ConfigureAwait(false);
        if (fault is not null)
        {
            await ((Task)ApplyFault(fault, method, args)!).ConfigureAwait(false);
            return;
        }

        try
        {
            await ((Task)InvokeTarget(method, args)!).ConfigureAwait(false);
            _engine.CallSucceeded(call);
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private async Task<T> InvokeTaskOfTAsync<T>(MethodInfo method, object?[] args, ChaosCall call)
    {
        var fault = await _engine.BeforeCallAsync(call, FindToken(args)).ConfigureAwait(false);
        if (fault is not null)
        {
            return await ((Task<T>)ApplyFault(fault, method, args)!).ConfigureAwait(false);
        }

        try
        {
            var result = await ((Task<T>)InvokeTarget(method, args)!).ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return (T)Wrap(method, result)!;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private async ValueTask InvokeValueTaskAsync(MethodInfo method, object?[] args, ChaosCall call)
    {
        var fault = await _engine.BeforeCallAsync(call, FindToken(args)).ConfigureAwait(false);
        if (fault is not null)
        {
            await ((ValueTask)ApplyFault(fault, method, args)!).ConfigureAwait(false);
            return;
        }

        try
        {
            await ((ValueTask)InvokeTarget(method, args)!).ConfigureAwait(false);
            _engine.CallSucceeded(call);
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private async ValueTask<T> InvokeValueTaskOfTAsync<T>(MethodInfo method, object?[] args, ChaosCall call)
    {
        var fault = await _engine.BeforeCallAsync(call, FindToken(args)).ConfigureAwait(false);
        if (fault is not null)
        {
            return await ((ValueTask<T>)ApplyFault(fault, method, args)!).ConfigureAwait(false);
        }

        try
        {
            var result = await ((ValueTask<T>)InvokeTarget(method, args)!).ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return (T)Wrap(method, result)!;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private object? ApplyFault(Fault fault, MethodInfo method, object?[] args) =>
        _options.ApplyFault is not null
            ? _options.ApplyFault(fault, method, args)
            : throw new NotSupportedException($"The fault '{fault.Name}' is not supported by this veneer.");

    private object? InvokeTarget(MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private object? Wrap(MethodInfo method, object? result) =>
        _options.WrapResult is null ? result : _options.WrapResult(method, result);

    private static CancellationToken FindToken(object?[] args)
    {
        foreach (var arg in args)
        {
            if (arg is CancellationToken token)
            {
                return token;
            }
        }

        return CancellationToken.None;
    }
}

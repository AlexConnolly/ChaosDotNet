namespace ChaosDotNet;

/// <summary>Sets up what a method that returns <typeparamref name="TResult"/> does. Each terminal method returns the subject.</summary>
public sealed class SetupBuilder<T, TResult>
    where T : class
{
    private readonly ChaosSubject<T> _subject;

    internal SetupBuilder(ChaosSubject<T> subject, SubjectSetup setup)
    {
        _subject = subject;
        Setup = setup;
    }

    internal SubjectSetup Setup { get; }

    /// <summary>Returns a value.</summary>
    public ChaosSubject<T> Returns(TResult value) => Result(_ => value);

    /// <summary>Returns a value built on each call.</summary>
    public ChaosSubject<T> Returns(Func<TResult> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Result(_ => value());
    }

    /// <summary>Returns a value built from the first argument.</summary>
    public ChaosSubject<T> Returns<T1>(Func<T1, TResult> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Result(a => value((T1)a[0]!));
    }

    /// <summary>Returns a value built from the first two arguments.</summary>
    public ChaosSubject<T> Returns<T1, T2>(Func<T1, T2, TResult> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Result(a => value((T1)a[0]!, (T2)a[1]!));
    }

    /// <summary>Returns a value built from the first three arguments.</summary>
    public ChaosSubject<T> Returns<T1, T2, T3>(Func<T1, T2, T3, TResult> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Result(a => value((T1)a[0]!, (T2)a[1]!, (T3)a[2]!));
    }

    /// <summary>Returns one value per call, in order, then the last value for every call after.</summary>
    public ChaosSubject<T> ReturnsInOrder(params TResult[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Length);
        Setup.InOrder(values.Select(v => (Func<object?[], object?>)(_ => v)));
        return _subject;
    }

    /// <summary>Throws a new <typeparamref name="TException"/> on every matching call. Async methods return a faulted task.</summary>
    public ChaosSubject<T> Throws<TException>()
        where TException : Exception, new() => Throws(() => new TException());

    /// <summary>Throws a new exception from <paramref name="exception"/> on every matching call. Async methods return a faulted task.</summary>
    public ChaosSubject<T> Throws(Func<Exception> exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Setup.Exception = exception;
        return _subject;
    }

    /// <summary>Runs code on each matching call, before the result. Follow it with <c>Returns</c> or <c>Throws</c>, or leave the default result.</summary>
    public SetupBuilder<T, TResult> Callback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Setup.Callback = _ => callback();
        return this;
    }

    /// <summary>Runs code with the first argument on each matching call.</summary>
    public SetupBuilder<T, TResult> Callback<T1>(Action<T1> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Setup.Callback = a => callback((T1)a[0]!);
        return this;
    }

    /// <summary>Runs code with the first two arguments on each matching call.</summary>
    public SetupBuilder<T, TResult> Callback<T1, T2>(Action<T1, T2> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Setup.Callback = a => callback((T1)a[0]!, (T2)a[1]!);
        return this;
    }

    /// <summary>Ends the setup with the default result, for a setup that only has a callback.</summary>
    public ChaosSubject<T> Done() => _subject;

    internal ChaosSubject<T> Result(Func<object?[], object?> result)
    {
        Setup.Result = result;
        return _subject;
    }
}

/// <summary>Sets up what a method that returns nothing does. Each terminal method returns the subject.</summary>
public sealed class VoidSetupBuilder<T>
    where T : class
{
    private readonly ChaosSubject<T> _subject;
    private readonly SubjectSetup _setup;

    internal VoidSetupBuilder(ChaosSubject<T> subject, SubjectSetup setup)
    {
        _subject = subject;
        _setup = setup;
    }

    /// <summary>Throws a new <typeparamref name="TException"/> on every matching call.</summary>
    public ChaosSubject<T> Throws<TException>()
        where TException : Exception, new() => Throws(() => new TException());

    /// <summary>Throws a new exception from <paramref name="exception"/> on every matching call.</summary>
    public ChaosSubject<T> Throws(Func<Exception> exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _setup.Exception = exception;
        return _subject;
    }

    /// <summary>Runs code on each matching call.</summary>
    public ChaosSubject<T> Callback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _setup.Callback = _ => callback();
        return _subject;
    }

    /// <summary>Runs code with the first argument on each matching call.</summary>
    public ChaosSubject<T> Callback<T1>(Action<T1> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _setup.Callback = a => callback((T1)a[0]!);
        return _subject;
    }

    /// <summary>Runs code with the first two arguments on each matching call.</summary>
    public ChaosSubject<T> Callback<T1, T2>(Action<T1, T2> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _setup.Callback = a => callback((T1)a[0]!, (T2)a[1]!);
        return _subject;
    }

    /// <summary>Does nothing on matching calls. The same as having no setup on a loose subject, but allowed on a strict one.</summary>
    public ChaosSubject<T> DoesNothing() => _subject;
}

/// <summary>
/// <c>Returns</c> overloads for async methods that take the plain value and wrap it in a completed <see cref="Task{TResult}"/> or
/// <see cref="ValueTask{TResult}"/>. <c>ReturnsAsync</c> is the same, for people used to Moq.
/// </summary>
public static class AsyncSetupExtensions
{
    /// <summary>Returns a completed task holding <paramref name="value"/>.</summary>
    public static ChaosSubject<T> Returns<T, TValue>(this SetupBuilder<T, Task<TValue>> setup, TValue value)
        where T : class => Check(setup).Result(_ => Task.FromResult(value));

    /// <summary>Returns a completed task holding a value built from the first argument.</summary>
    public static ChaosSubject<T> Returns<T, TValue, T1>(this SetupBuilder<T, Task<TValue>> setup, Func<T1, TValue> value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Check(setup).Result(a => Task.FromResult(value((T1)a[0]!)));
    }

    /// <summary>Returns a completed task holding a value built from the first two arguments.</summary>
    public static ChaosSubject<T> Returns<T, TValue, T1, T2>(this SetupBuilder<T, Task<TValue>> setup, Func<T1, T2, TValue> value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Check(setup).Result(a => Task.FromResult(value((T1)a[0]!, (T2)a[1]!)));
    }

    /// <summary>Returns a completed task holding a value built from the first three arguments.</summary>
    public static ChaosSubject<T> Returns<T, TValue, T1, T2, T3>(this SetupBuilder<T, Task<TValue>> setup, Func<T1, T2, T3, TValue> value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Check(setup).Result(a => Task.FromResult(value((T1)a[0]!, (T2)a[1]!, (T3)a[2]!)));
    }

    /// <summary>Returns one completed task per call, holding each value in order, then the last value for every call after.</summary>
    public static ChaosSubject<T> ReturnsInOrder<T, TValue>(this SetupBuilder<T, Task<TValue>> setup, params TValue[] values)
        where T : class => Check(setup).ReturnsInOrder(values.Select(Task.FromResult).ToArray());

    /// <summary>Returns one completed value task per call, holding each value in order, then the last value for every call after.</summary>
    public static ChaosSubject<T> ReturnsInOrder<T, TValue>(this SetupBuilder<T, ValueTask<TValue>> setup, params TValue[] values)
        where T : class => Check(setup).ReturnsInOrder(values.Select(v => new ValueTask<TValue>(v)).ToArray());

    /// <summary>Returns a completed value task holding <paramref name="value"/>.</summary>
    public static ChaosSubject<T> Returns<T, TValue>(this SetupBuilder<T, ValueTask<TValue>> setup, TValue value)
        where T : class => Check(setup).Result(_ => new ValueTask<TValue>(value));

    /// <summary>Returns a completed value task holding a value built from the first argument.</summary>
    public static ChaosSubject<T> Returns<T, TValue, T1>(this SetupBuilder<T, ValueTask<TValue>> setup, Func<T1, TValue> value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Check(setup).Result(a => new ValueTask<TValue>(value((T1)a[0]!)));
    }

    /// <summary>Returns a completed value task holding a value built from the first two arguments.</summary>
    public static ChaosSubject<T> Returns<T, TValue, T1, T2>(this SetupBuilder<T, ValueTask<TValue>> setup, Func<T1, T2, TValue> value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Check(setup).Result(a => new ValueTask<TValue>(value((T1)a[0]!, (T2)a[1]!)));
    }

    /// <summary>The same as <c>Returns(value)</c> on a <see cref="Task{TResult}"/> method.</summary>
    public static ChaosSubject<T> ReturnsAsync<T, TValue>(this SetupBuilder<T, Task<TValue>> setup, TValue value)
        where T : class => setup.Returns(value);

    /// <summary>The same as <c>Returns(value)</c> on a <see cref="Task{TResult}"/> method, built from the first argument.</summary>
    public static ChaosSubject<T> ReturnsAsync<T, TValue, T1>(this SetupBuilder<T, Task<TValue>> setup, Func<T1, TValue> value)
        where T : class => setup.Returns(value);

    /// <summary>The same as <c>Returns(value)</c> on a <see cref="Task{TResult}"/> method, built from the first two arguments.</summary>
    public static ChaosSubject<T> ReturnsAsync<T, TValue, T1, T2>(this SetupBuilder<T, Task<TValue>> setup, Func<T1, T2, TValue> value)
        where T : class => setup.Returns(value);

    /// <summary>The same as <c>Returns(value)</c> on a <see cref="ValueTask{TResult}"/> method.</summary>
    public static ChaosSubject<T> ReturnsAsync<T, TValue>(this SetupBuilder<T, ValueTask<TValue>> setup, TValue value)
        where T : class => setup.Returns(value);

    private static SetupBuilder<T, TResult> Check<T, TResult>(SetupBuilder<T, TResult> setup)
        where T : class => setup ?? throw new ArgumentNullException(nameof(setup));
}

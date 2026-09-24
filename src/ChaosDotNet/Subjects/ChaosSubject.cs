using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using ChaosDotNet.Proxies;

namespace ChaosDotNet;

/// <summary>A call made to a <see cref="ChaosSubject{T}"/>.</summary>
public sealed class SubjectCall : ProxyChaosCall
{
    /// <summary>Creates a call.</summary>
    public SubjectCall(MethodInfo method, object?[] arguments)
        : base(method, arguments)
    {
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Method.Name}({string.Join(", ", Arguments.Select(a => a is string text ? $"\"{text}\"" : a?.ToString() ?? "null"))})";
}

/// <summary>Thrown by a strict <see cref="ChaosSubject{T}"/> when a call has no matching setup.</summary>
public sealed class ChaosSubjectException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChaosSubjectException(string message)
        : base(message)
    {
    }
}

/// <summary>How many times <see cref="ChaosSubject{T}.Received"/> expects a call.</summary>
public readonly record struct Times(int Min, int Max)
{
    /// <summary>Exactly once.</summary>
    public static Times Once => new(1, 1);

    /// <summary>Never.</summary>
    public static Times Never => new(0, 0);

    /// <summary>Exactly <paramref name="count"/> times.</summary>
    public static Times Exactly(int count) => new(count, count);

    /// <summary>At least <paramref name="count"/> times.</summary>
    public static Times AtLeast(int count) => new(count, int.MaxValue);

    /// <summary>At most <paramref name="count"/> times.</summary>
    public static Times AtMost(int count) => new(0, count);

    /// <inheritdoc />
    public override string ToString() =>
        Min == Max ? $"exactly {Min}" : Max == int.MaxValue ? $"at least {Min}" : Min == 0 ? $"at most {Max}" : $"{Min} to {Max}";
}

/// <summary>
/// A chaotic mock of any interface. Set up what methods return with <see cref="Setup{TResult}"/>, add chaos with the timeline
/// methods (filtered with <see cref="When{TResult}(Expression{Func{T, TResult}})"/>), then call <see cref="Create()"/>.
/// </summary>
/// <example>
/// <code>
/// var gateway = new ChaosSubject&lt;IPaymentGateway&gt;()
///     .Setup(g => g.ChargeAsync(Arg.Any&lt;string&gt;(), Arg.Any&lt;decimal&gt;())).Returns(new Receipt("ok"))
///     .When(g => g.ChargeAsync(Arg.Any&lt;string&gt;(), Arg.Any&lt;decimal&gt;())).ForCalls(2).Fail&lt;TimeoutException&gt;();
///
/// IPaymentGateway payments = gateway.Create();
/// </code>
/// </example>
/// <typeparam name="T">The interface to mock.</typeparam>
public sealed class ChaosSubject<T> : ChaosFactory<ChaosSubject<T>, SubjectCall>
    where T : class
{
    private readonly object _gate = new();
    private readonly List<SubjectSetup> _setups = [];
    private readonly List<SubjectCall> _calls = [];
    private readonly bool _strict;

    /// <summary>Creates a subject with its own clock and seed.</summary>
    /// <param name="clock">The clock the timeline runs on.</param>
    /// <param name="seed">The seed for flaky windows and odd values.</param>
    /// <param name="strict">When <see langword="true"/>, calls with no matching setup throw <see cref="ChaosSubjectException"/> instead of returning defaults.</param>
    public ChaosSubject(TimeProvider? clock = null, int? seed = null, bool strict = false)
        : base(clock, seed)
    {
        _strict = strict;
        EnsureInterface();
    }

    /// <summary>Creates a subject that shares the scenario's clock and start time.</summary>
    public ChaosSubject(ChaosScenario scenario, bool strict = false)
        : base(scenario)
    {
        _strict = strict;
        EnsureInterface();
    }

    /// <summary>Every call received so far, in order.</summary>
    public IReadOnlyList<SubjectCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>Starts a setup for a method that returns a value, or a property.</summary>
    public SetupBuilder<T, TResult> Setup<TResult>(Expression<Func<T, TResult>> call) => new(this, AddSetup(call));

    /// <summary>Starts a setup for a method that returns nothing.</summary>
    public VoidSetupBuilder<T> Setup(Expression<Action<T>> call) => new(this, AddSetup(call));

    /// <summary>Limits the next window to calls that match the expression, for example <c>g => g.GetAsync(Arg.Any&lt;int&gt;())</c>.</summary>
    public ChaosSubject<T> When<TResult>(Expression<Func<T, TResult>> call)
    {
        var pattern = CallPattern.From(call);
        return When(c => pattern.Matches(c.Method, c.Arguments));
    }

    /// <summary>Limits the next window to calls that match the expression, for a method that returns nothing.</summary>
    public ChaosSubject<T> When(Expression<Action<T>> call)
    {
        var pattern = CallPattern.From(call);
        return When(c => pattern.Matches(c.Method, c.Arguments));
    }

    /// <summary>Creates the mock and starts the timeline if it has not started.</summary>
    public T Create() => Create(null);

    /// <summary>
    /// Creates a partial mock over a real object: calls with a matching setup use the setup, others go to <paramref name="inner"/>.
    /// Starts the timeline if it has not started.
    /// </summary>
    public T Create(T? inner)
    {
        var behaviour = DispatchProxy.Create<T, SubjectBehaviourProxy>();
        ((SubjectBehaviourProxy)(object)behaviour).Initialise(this, inner);
        var veneer = ChaosProxy.Create(behaviour, Engine, new ChaosProxyOptions
        {
            CreateCall = (method, args) => Record(new SubjectCall(method, args)),
            ApplyFault = OddValueFault.Apply,
        });
        Engine.Start();
        return veneer;
    }

    /// <summary>Checks how many calls matched the expression. Throws <see cref="ChaosAssertionException"/> when the count is outside <paramref name="times"/>.</summary>
    public ChaosSubject<T> Received<TResult>(Expression<Func<T, TResult>> call, Times times) => Check(CallPattern.From(call), times);

    /// <summary>Checks how many calls matched the expression, for a method that returns nothing.</summary>
    public ChaosSubject<T> Received(Expression<Action<T>> call, Times times) => Check(CallPattern.From(call), times);

    /// <summary>Checks that no call matched the expression.</summary>
    public ChaosSubject<T> DidNotReceive<TResult>(Expression<Func<T, TResult>> call) => Check(CallPattern.From(call), Times.Never);

    /// <summary>Checks that no call matched the expression, for a method that returns nothing.</summary>
    public ChaosSubject<T> DidNotReceive(Expression<Action<T>> call) => Check(CallPattern.From(call), Times.Never);

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        .. base.DefaultMonkeyFaults(),
        MonkeyFault.RandomException(
            "RandomException",
            MonkeyFaultKind.Error,
            () => new TimeoutException("The operation has timed out. (ChaosDotNet)"),
            () => new IOException("Unable to read data from the transport connection. (ChaosDotNet)"),
            () => new InvalidOperationException("The dependency returned an unexpected response. (ChaosDotNet)")),
        OddValueFault.Monkey,
    ];

    internal object? Invoke(MethodInfo method, object?[] args, T? inner)
    {
        SubjectSetup? setup;
        lock (_gate)
        {
            setup = _setups.LastOrDefault(s => s.Pattern.Matches(method, args));
        }

        if (setup is not null)
        {
            return setup.Run(method, args);
        }

        if (inner is not null)
        {
            try
            {
                return method.Invoke(inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        if (_strict)
        {
            throw new ChaosSubjectException($"{typeof(T).Name}.{new SubjectCall(method, args)} has no setup, and the subject is strict.");
        }

        return OddValues.Default(method.ReturnType);
    }

    private SubjectSetup AddSetup(LambdaExpression call)
    {
        var setup = new SubjectSetup(CallPattern.From(call));
        lock (_gate)
        {
            _setups.Add(setup);
        }

        return setup;
    }

    private SubjectCall Record(SubjectCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }

        return call;
    }

    private ChaosSubject<T> Check(CallPattern pattern, Times times)
    {
        var calls = Calls;
        var count = calls.Count(c => pattern.Matches(c.Method, c.Arguments));
        if (count < times.Min || count > times.Max)
        {
            var text = new StringBuilder($"Expected {times} call(s) matching {pattern.Text}, but there were {count}.");
            text.AppendLine().Append("Calls received:");
            foreach (var call in calls)
            {
                text.AppendLine().Append("  ").Append(call);
            }

            throw new ChaosAssertionException(text.ToString());
        }

        return this;
    }

    private static void EnsureInterface()
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException($"{typeof(T).Name} is not an interface. ChaosSubject<T> mocks interfaces only.", nameof(T));
        }
    }
}

/// <summary>The <see cref="DispatchProxy"/> that runs a subject's setups. Not for direct use.</summary>
public class SubjectBehaviourProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _invoke = null!;

    internal void Initialise<T>(ChaosSubject<T> subject, T? inner)
        where T : class => _invoke = (method, args) => subject.Invoke(method, args, inner);

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args ?? []);
}

internal sealed class SubjectSetup(CallPattern pattern)
{
    private readonly object _gate = new();
    private Queue<Func<object?[], object?>>? _sequence;

    public CallPattern Pattern { get; } = pattern;

    public Func<object?[], object?>? Result { get; set; }

    public Func<Exception>? Exception { get; set; }

    public Action<object?[]>? Callback { get; set; }

    public void InOrder(IEnumerable<Func<object?[], object?>> results)
    {
        lock (_gate)
        {
            _sequence = new Queue<Func<object?[], object?>>(results);
        }
    }

    public object? Run(MethodInfo method, object?[] args)
    {
        Callback?.Invoke(args);

        if (Exception is not null)
        {
            var error = Exception();
            return method.ReturnType == typeof(Task) || OddValues.TaskResultType(method.ReturnType) is not null || method.ReturnType == typeof(ValueTask)
                ? Faulted(method.ReturnType, error)
                : throw error;
        }

        Func<object?[], object?>? result;
        lock (_gate)
        {
            result = _sequence is { Count: > 1 } ? _sequence.Dequeue() : _sequence?.Peek() ?? Result;
        }

        return result is null ? OddValues.Default(method.ReturnType) : result(args);
    }

    private static object Faulted(Type returnType, Exception error)
    {
        if (returnType == typeof(Task))
        {
            return Task.FromException(error);
        }

        if (returnType == typeof(ValueTask))
        {
            return ValueTask.FromException(error);
        }

        var resultType = OddValues.TaskResultType(returnType)!;
        var faulted = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(resultType).Invoke(null, [error])!;
        return returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? faulted
            : returnType.GetConstructor([typeof(Task<>).MakeGenericType(resultType)])!.Invoke([faulted]);
    }
}

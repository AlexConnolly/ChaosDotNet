namespace ChaosDotNet;

/// <summary>A window waiting for its fault. Each method adds the window to the timeline and returns the factory.</summary>
public sealed class WindowBuilder<TSelf, TCall>
    where TSelf : ChaosFactory<TSelf, TCall>
    where TCall : ChaosCall
{
    private readonly TSelf _factory;
    private readonly Segment _segment;
    private bool _used;

    internal WindowBuilder(TSelf factory, Segment segment)
    {
        _factory = factory;
        _segment = segment;
    }

    /// <summary>The factory the window belongs to.</summary>
    public TSelf Factory => _factory;

    /// <summary>Calls block until the window ends, then continue to the real client. The call's <see cref="CancellationToken"/> still works.</summary>
    public TSelf Freeze() => Inject(FreezeFault.Instance);

    /// <summary>Each call waits for a fixed delay, then continues to the real client.</summary>
    public TSelf Latency(double amount, TimeUnit unit) => Inject(new LatencyFault(unit.ToTimeSpan(amount)));

    /// <summary>Each call waits for a random delay between two bounds, then continues to the real client.</summary>
    public TSelf Jitter(double min, double max, TimeUnit unit) =>
        Inject(new JitterFault(unit.ToTimeSpan(min, nameof(min)), unit.ToTimeSpan(max, nameof(max))));

    /// <summary>Each call throws a new exception from the factory method and does not reach the real client.</summary>
    public TSelf Fail(Func<Exception> exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Inject(new FailFault(_ => exception()));
    }

    /// <summary>Each call throws a new exception built from the call and does not reach the real client.</summary>
    public TSelf Fail(Func<TCall, Exception> exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Inject(new FailFault(call => exception((TCall)call)));
    }

    /// <summary>Each call throws a new <typeparamref name="TException"/> and does not reach the real client.</summary>
    public TSelf Fail<TException>()
        where TException : Exception, new() => Inject(new FailFault(_ => new TException()));

    /// <summary>Each call throws an exception picked at random from <paramref name="exceptions"/>, using the factory's seed.</summary>
    public TSelf FailRandomly(params Func<Exception>[] exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentOutOfRangeException.ThrowIfZero(exceptions.Length);
        return Inject(MonkeyFault.RandomFail(new Random(_factory.Engine.Seed), exceptions));
    }

    /// <summary>Applies any fault. Factories use this to add their own faults as extension methods.</summary>
    public TSelf Inject(Fault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (_used)
        {
            throw new InvalidOperationException("This window already has a fault. Start a new window with For(...), ForCalls(...), Until(...) or Forever().");
        }

        _used = true;
        _segment.Fault = fault;
        return _factory.AddWindow(_segment);
    }
}

/// <summary>A repeating window waiting for its duration.</summary>
public sealed class RepeatBuilder<TSelf, TCall>
    where TSelf : ChaosFactory<TSelf, TCall>
    where TCall : ChaosCall
{
    private readonly TSelf _factory;
    private readonly TimeSpan _period;

    internal RepeatBuilder(TSelf factory, TimeSpan period)
    {
        _factory = factory;
        _period = period;
    }

    /// <summary>Sets how long the window is active at the start of each period.</summary>
    public WindowBuilder<TSelf, TCall> For(double amount, TimeUnit unit)
    {
        var duration = unit.ToTimeSpan(amount);
        if (duration >= _period)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The window must be shorter than the period. Use Forever() for a window that never ends.");
        }

        return new WindowBuilder<TSelf, TCall>(_factory, new Segment
        {
            Kind = SegmentKind.Repeating,
            Period = _period,
            Duration = duration,
            When = _factory.TakePendingWhen(),
        });
    }
}

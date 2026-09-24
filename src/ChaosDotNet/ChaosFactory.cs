namespace ChaosDotNet;

/// <summary>
/// The base of every factory. It holds a timeline of windows and creates veneers that follow it.
/// Derive from it to build a factory for your own client.
/// </summary>
/// <typeparam name="TSelf">The derived factory type, so that the fluent methods return it.</typeparam>
/// <typeparam name="TCall">The call type that the factory's veneers pass to <c>When(...)</c> filters.</typeparam>
public abstract class ChaosFactory<TSelf, TCall>
    where TSelf : ChaosFactory<TSelf, TCall>
    where TCall : ChaosCall
{
    private Func<ChaosCall, bool>? _pendingWhen;

    /// <summary>Creates a factory with its own clock and seed.</summary>
    /// <param name="clock">The clock the timeline runs on. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="seed">The seed for <c>Flaky</c> and <c>Jitter</c>. Defaults to a random seed, shown in <see cref="Seed"/>.</param>
    protected ChaosFactory(TimeProvider? clock = null, int? seed = null)
    {
        Engine = new ChaosEngine(clock, seed);
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    protected ChaosFactory(ChaosScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Engine = new ChaosEngine(scenario);
    }

    /// <summary>The engine that runs the timeline. Veneers use it; tests normally do not.</summary>
    public ChaosEngine Engine { get; }

    /// <summary>The seed in use. Pass it back to the constructor to repeat a run.</summary>
    public int Seed => Engine.Seed;

    /// <summary>A snapshot of the timeline log.</summary>
    public IReadOnlyList<ChaosEvent> Log => Engine.Log;

    private TSelf Self => (TSelf)this;

    /// <summary>Starts the timeline now. Without this, the timeline starts on the first <c>Create...</c> call.</summary>
    public TSelf Start()
    {
        Engine.Start();
        return Self;
    }

    /// <summary>Checks what happened on the timeline.</summary>
    public ChaosVerifier Verify() => new(Engine);

    /// <summary>Does nothing. Use it to make a chain of windows easier to read.</summary>
    public TSelf Then() => Self;

    /// <summary>Lets calls through for an amount of time before the next window.</summary>
    public TSelf After(double amount, TimeUnit unit)
    {
        EnsureNoPendingFilter(nameof(After));
        Engine.AddSegment(Segment.Gap(unit.ToTimeSpan(amount)));
        return Self;
    }

    /// <summary>Limits the next window to calls that match. Other calls pass through and do not count towards the window.</summary>
    public TSelf When(Func<TCall, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        EnsureNoPendingFilter(nameof(When));
        _pendingWhen = call => call is TCall typed && predicate(typed);
        return Self;
    }

    /// <summary>Starts a window that lasts an amount of time.</summary>
    public WindowBuilder<TSelf, TCall> For(double amount, TimeUnit unit) =>
        Window(WindowEnd.Duration, duration: unit.ToTimeSpan(amount));

    /// <summary>Starts a window that lasts for a number of matching calls.</summary>
    public WindowBuilder<TSelf, TCall> ForCalls(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return Window(WindowEnd.Calls, calls: count);
    }

    /// <summary>
    /// Starts a window that lasts until a condition becomes true. The condition is checked on each call
    /// and while calls are frozen. It must not call a veneer from this factory.
    /// </summary>
    public WindowBuilder<TSelf, TCall> Until(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Window(WindowEnd.Until, until: condition);
    }

    /// <summary>Starts a window that never ends. Nothing can follow it.</summary>
    public WindowBuilder<TSelf, TCall> Forever() => Window(WindowEnd.Forever);

    /// <summary>Starts a repeating window: once per period, for the duration given to <see cref="RepeatBuilder{TSelf,TCall}.For"/>. Nothing can follow it.</summary>
    public RepeatBuilder<TSelf, TCall> Every(double period, TimeUnit unit)
    {
        var span = unit.ToTimeSpan(period, nameof(period));
        if (span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be more than zero.");
        }

        return new RepeatBuilder<TSelf, TCall>(Self, span);
    }

    /// <summary>Applies the last window's fault to only a proportion of its calls, chosen with the factory's seed.</summary>
    /// <param name="rate">A value from 0 to 1. For example 0.2 applies the fault to about one call in five.</param>
    public TSelf Flaky(double rate)
    {
        if (double.IsNaN(rate) || rate < 0 || rate > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be from 0 to 1.");
        }

        Engine.LastWindow().Rate = rate;
        return Self;
    }

    /// <summary>Sets how long an unbounded freeze (<c>ForCalls</c>, <c>Until</c>, <c>Forever</c>) lasts before the call throws <see cref="ChaosFreezeTimeoutException"/>. The default is 60 seconds.</summary>
    public TSelf WithMaxFreeze(double amount, TimeUnit unit)
    {
        Engine.MaxFreeze = unit.ToTimeSpan(amount);
        return Self;
    }

    internal TSelf AddWindow(Segment segment)
    {
        Engine.AddSegment(segment);
        return Self;
    }

    internal Func<ChaosCall, bool>? TakePendingWhen()
    {
        var when = _pendingWhen;
        _pendingWhen = null;
        return when;
    }

    private WindowBuilder<TSelf, TCall> Window(WindowEnd end, TimeSpan duration = default, int calls = 0, Func<bool>? until = null) =>
        new(Self, new Segment
        {
            Kind = SegmentKind.Window,
            End = end,
            Duration = duration,
            Calls = calls,
            Until = until,
            When = TakePendingWhen(),
        });

    private void EnsureNoPendingFilter(string method)
    {
        if (_pendingWhen is not null)
        {
            throw new InvalidOperationException($"When(...) must be followed by a window, not {method}(...).");
        }
    }
}

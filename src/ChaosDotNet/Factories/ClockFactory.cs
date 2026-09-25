namespace ChaosDotNet.Factories;

/// <summary>A read of a <see cref="ClockFactory"/> veneer. <see cref="ChaosCall.Operation"/> is <c>Now</c>, <c>Timestamp</c>, <c>TimeZone</c> or <c>Timer</c>.</summary>
public sealed class ClockCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    public ClockCall(string operation)
        : base(operation)
    {
    }
}

/// <summary>
/// Builds a chaotic <see cref="TimeProvider"/> for the app: time that jumps, goes backwards, drifts, stands still,
/// shifts its time zone offset, or fires timers late. When a window ends the clock snaps back to real time.
/// </summary>
/// <example>
/// <code>
/// var clock = new ClockFactory()
///     .After(10, TimeUnit.Seconds).For(30, TimeUnit.Seconds).JumpBackward(5, TimeUnit.Minutes);
///
/// services.AddSingleton&lt;TimeProvider&gt;(clock.Create());
/// </code>
/// </example>
public sealed class ClockFactory : ChaosFactory<ClockFactory, ClockCall>
{
    /// <summary>Creates a factory. <paramref name="clock"/> is the real clock the veneer bends, and the clock the timeline runs on.</summary>
    public ClockFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time. The veneer bends the scenario's clock.</summary>
    public ClockFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates a chaotic <see cref="TimeProvider"/> over the factory's clock and starts the timeline if it has not started.</summary>
    public TimeProvider Create() => Create(Engine.Clock);

    /// <summary>
    /// Creates a chaotic <see cref="TimeProvider"/> over <paramref name="inner"/> and starts the timeline if it has not started.
    /// The timeline's windows still follow the factory's clock.
    /// </summary>
    public TimeProvider Create(TimeProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var veneer = new ChaosTimeProvider(inner, Engine);
        Engine.Start();
        return veneer;
    }

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        new("JumpForward", MonkeyFaultKind.Weird, random => ClockFault.Jump(TimeSpan.FromMinutes(1 + random.Next(120)))),
        new("JumpBackward", MonkeyFaultKind.Weird, random => ClockFault.Jump(-TimeSpan.FromMinutes(1 + random.Next(120)))),
        new("Drift", MonkeyFaultKind.Weird, random => ClockFault.Drift(0.5 + random.NextDouble())),
        new("Stall", MonkeyFaultKind.Weird, _ => ClockFault.Stall()),
        new("DaylightSaving", MonkeyFaultKind.Weird, random => ClockFault.DaylightSaving(random.Next(2) == 0 ? 1 : -1)),
        new("LateTimers", MonkeyFaultKind.Slowness, random => ClockFault.LateTimers(TimeSpan.FromSeconds(1 + random.Next(30)))),
    ];
}

/// <summary>Clock faults for a <see cref="ClockFactory"/> window.</summary>
public static class ClockFaults
{
    /// <summary><c>GetUtcNow()</c> is ahead by the amount.</summary>
    public static ClockFactory JumpForward(this WindowBuilder<ClockFactory, ClockCall> window, double amount, TimeUnit unit)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(ClockFault.Jump(unit.ToTimeSpan(amount)));
    }

    /// <summary><c>GetUtcNow()</c> is behind by the amount: time goes backwards.</summary>
    public static ClockFactory JumpBackward(this WindowBuilder<ClockFactory, ClockCall> window, double amount, TimeUnit unit)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(ClockFault.Jump(-unit.ToTimeSpan(amount)));
    }

    /// <summary>Time runs fast (for example 1.2) or slow (0.8) from the first read in the window. <c>GetTimestamp()</c> drifts too.</summary>
    public static ClockFactory Drift(this WindowBuilder<ClockFactory, ClockCall> window, double factor)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (double.IsNaN(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be more than zero.");
        }

        return window.Inject(ClockFault.Drift(factor));
    }

    /// <summary>Time stands still from the first read in the window: <c>GetUtcNow()</c> and <c>GetTimestamp()</c> return the same values.</summary>
    public static ClockFactory Stall(this WindowBuilder<ClockFactory, ClockCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(ClockFault.Stall());
    }

    /// <summary>
    /// The local time zone's offset shifts by the hours, as on a daylight saving change. UTC time is unchanged. The shift is from
    /// -12 to 12 hours, and the resulting offset is kept inside the valid range of -14 to 14 hours.
    /// </summary>
    public static ClockFactory DaylightSaving(this WindowBuilder<ClockFactory, ClockCall> window, int hours = 1)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Math.Abs(hours), 12, nameof(hours));
        return window.Inject(ClockFault.DaylightSaving(hours));
    }

    /// <summary>Timers and delays created in the window fire late by the amount.</summary>
    public static ClockFactory LateTimers(this WindowBuilder<ClockFactory, ClockCall> window, double amount, TimeUnit unit)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(ClockFault.LateTimers(unit.ToTimeSpan(amount)));
    }

    /// <summary><c>GetTimestamp()</c> can go backwards, which is rare but real on some virtual machines.</summary>
    public static ClockFactory NonMonotonic(this WindowBuilder<ClockFactory, ClockCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(ClockFault.NonMonotonic(new Random(window.Factory.Seed)));
    }
}

/// <summary>A fault that bends a <see cref="ClockFactory"/> veneer's time.</summary>
public sealed class ClockFault : Fault
{
    private readonly object _gate = new();
    private readonly string[] _operations;
    private readonly Func<ClockFault, DateTimeOffset, DateTimeOffset> _now;
    private readonly Func<ClockFault, long, long, long> _timestamp;
    private DateTimeOffset? _anchorNow;
    private long? _anchorTimestamp;

    private ClockFault(
        string name,
        string[] operations,
        Func<ClockFault, DateTimeOffset, DateTimeOffset>? now = null,
        Func<ClockFault, long, long, long>? timestamp = null)
    {
        Name = name;
        _operations = operations;
        _now = now ?? ((_, value) => value);
        _timestamp = timestamp ?? ((_, value, _) => value);
    }

    /// <inheritdoc />
    public override string Name { get; }

    internal TimeSpan ZoneShift { get; private init; }

    internal TimeSpan TimerDelay { get; private init; }

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call is ClockCall && _operations.Contains(call.Operation);

    internal static ClockFault Jump(TimeSpan by) =>
        new(by >= TimeSpan.Zero ? "JumpForward" : "JumpBackward", ["Now"], now: (_, value) => value + by);

    internal static ClockFault Drift(double factor) => new(
        $"Drift({factor:0.##})",
        ["Now", "Timestamp"],
        now: (fault, value) =>
        {
            var anchor = fault.AnchorNow(value);
            return anchor + TimeSpan.FromTicks((long)((value - anchor).Ticks * factor));
        },
        timestamp: (fault, value, _) =>
        {
            var anchor = fault.AnchorTimestamp(value);
            return anchor + (long)((value - anchor) * factor);
        });

    internal static ClockFault Stall() => new(
        "Stall",
        ["Now", "Timestamp"],
        now: (fault, value) => fault.AnchorNow(value),
        timestamp: (fault, value, _) => fault.AnchorTimestamp(value));

    internal static ClockFault DaylightSaving(int hours) => new($"DaylightSaving({hours:+0;-0})", ["TimeZone"]) { ZoneShift = TimeSpan.FromHours(hours) };

    internal static ClockFault LateTimers(TimeSpan by) => new("LateTimers", ["Timer"]) { TimerDelay = by };

    internal static ClockFault NonMonotonic(Random random) => new(
        "NonMonotonic",
        ["Timestamp"],
        timestamp: (_, value, frequency) =>
        {
            lock (random)
            {
                return value - (long)(random.NextDouble() * frequency);
            }
        });

    /// <inheritdoc />
    public override void Activated()
    {
        lock (_gate)
        {
            _anchorNow = null;
            _anchorTimestamp = null;
        }
    }

    internal DateTimeOffset BendNow(DateTimeOffset value) => _now(this, value);

    internal long BendTimestamp(long value, long frequency) => _timestamp(this, value, frequency);

    private DateTimeOffset AnchorNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            return _anchorNow ??= value;
        }
    }

    private long AnchorTimestamp(long value)
    {
        lock (_gate)
        {
            return _anchorTimestamp ??= value;
        }
    }
}

internal sealed class ChaosTimeProvider(TimeProvider inner, ChaosEngine engine) : TimeProvider
{
    private static readonly ClockCall Now = new("Now");
    private static readonly ClockCall Timestamp = new("Timestamp");
    private static readonly ClockCall Zone = new("TimeZone");
    private static readonly ClockCall Timer = new("Timer");

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone
    {
        get
        {
            var zone = inner.LocalTimeZone;
            if (Fault(Zone) is not { ZoneShift: var shift } || shift == TimeSpan.Zero)
            {
                return zone;
            }

            var limit = TimeSpan.FromHours(14);
            var offset = zone.BaseUtcOffset + shift;
            offset = offset > limit ? limit : offset < -limit ? -limit : offset;
            return TimeZoneInfo.CreateCustomTimeZone($"{zone.Id} (ChaosDotNet {shift.TotalHours:+0;-0}h)", offset, $"{zone.DisplayName} (shifted)", zone.StandardName);
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        var now = inner.GetUtcNow();
        return Fault(Now) is { } fault ? fault.BendNow(now) : now;
    }

    public override long GetTimestamp()
    {
        var timestamp = inner.GetTimestamp();
        return Fault(Timestamp) is { } fault ? fault.BendTimestamp(timestamp, inner.TimestampFrequency) : timestamp;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan && Fault(Timer) is { TimerDelay: var delay })
        {
            dueTime += delay;
        }

        return inner.CreateTimer(callback, state, dueTime, period);
    }

    private ClockFault? Fault(ClockCall call) => engine.BeforeCall(call) switch
    {
        null => null,
        ClockFault clock => clock,
        var other => throw new NotSupportedException($"The fault '{other.Name}' is not supported by ClockFactory."),
    };
}

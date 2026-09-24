using System.Runtime.CompilerServices;

namespace ChaosDotNet;

/// <summary>
/// Runs one factory's timeline. Veneers call <see cref="BeforeCallAsync"/> before each real call and
/// <see cref="CallSucceeded"/> or <see cref="CallFailed"/> after it.
/// You only need this type to build your own factory or veneer.
/// </summary>
public sealed class ChaosEngine
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly List<Segment> _segments = [];
    private readonly List<ChaosEvent> _log = [];
    private readonly Random _random;
    private readonly ChaosScenario? _scenario;
    private readonly ConditionalWeakTable<Exception, object> _injected = [];

    private DateTimeOffset? _startedAt;
    private int _index;
    private DateTimeOffset _segmentStart;
    private int _segmentCalls;
    private int _windowCount;
    private int _frozen;
    private volatile bool _started;

    internal ChaosEngine(TimeProvider? clock, int? seed)
    {
        Clock = clock ?? TimeProvider.System;
        Seed = seed ?? Random.Shared.Next();
        _random = new Random(Seed);
    }

    internal ChaosEngine(ChaosScenario scenario)
        : this(scenario.Clock, scenario.NextSeed())
    {
        _scenario = scenario;
        scenario.Register(this);
    }

    /// <summary>The clock the timeline runs on.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The seed for <c>Flaky</c> and <c>Jitter</c>. The same seed and calls give the same faults.</summary>
    public int Seed { get; }

    /// <summary>Unbounded freezes (<c>ForCalls</c>, <c>Until</c>, <c>Forever</c>) throw <see cref="ChaosFreezeTimeoutException"/> after this long.</summary>
    public TimeSpan MaxFreeze { get; internal set; } = TimeSpan.FromSeconds(60);

    /// <summary>When the timeline started, or <see langword="null"/> before it starts.</summary>
    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_gate)
            {
                return _startedAt;
            }
        }
    }

    /// <summary>The number of calls frozen right now.</summary>
    public int FrozenCalls => Volatile.Read(ref _frozen);

    /// <summary>A snapshot of the timeline log.</summary>
    public IReadOnlyList<ChaosEvent> Log
    {
        get
        {
            lock (_gate)
            {
                if (_startedAt is not null)
                {
                    Advance(Clock.GetUtcNow());
                }

                return _log.ToArray();
            }
        }
    }

    /// <summary>Starts the timeline now. Does nothing if it has already started.</summary>
    public void Start()
    {
        if (_scenario is not null)
        {
            _scenario.Start();
            return;
        }

        StartAt(Clock.GetUtcNow());
    }

    internal void StartAt(DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_startedAt is not null)
            {
                return;
            }

            _startedAt = at;
            _segmentStart = at;
            _log.Add(new ChaosEvent(at, ChaosEventKind.TimelineStarted));
            LogSegmentStarted(at);
            _started = true;
        }
    }

    internal void AddSegment(Segment segment)
    {
        lock (_gate)
        {
            if (_startedAt is not null)
            {
                throw new InvalidOperationException("The timeline has started. Configure all windows before the first Create call or Start().");
            }

            if (_segments.Count > 0 && _segments[^1] is { Kind: SegmentKind.Repeating } or { End: WindowEnd.Forever })
            {
                throw new InvalidOperationException("Forever() and Every(...) windows never end, so nothing can follow them.");
            }

            if (segment.Kind != SegmentKind.Gap)
            {
                segment.WindowIndex = _windowCount++;
            }

            _segments.Add(segment);
        }
    }

    internal Segment LastWindow()
    {
        lock (_gate)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                if (_segments[i].Kind != SegmentKind.Gap)
                {
                    return _segments[i];
                }
            }
        }

        throw new InvalidOperationException("Define a window (for example For(10, TimeUnit.Seconds).Freeze()) before this call.");
    }

    /// <summary>
    /// Applies the active fault to a call. Returns <see langword="null"/> when the call should go to the real client.
    /// Freeze, latency and jitter wait, then return <see langword="null"/>. Fail throws.
    /// Any other fault is returned for the veneer to apply.
    /// </summary>
    public async ValueTask<Fault?> BeforeCallAsync(ChaosCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var decision = Decide(call);
        if (decision.Fault is null)
        {
            return null;
        }

        switch (decision.Fault)
        {
            case FreezeFault:
                await FreezeAsync(decision, cancellationToken).ConfigureAwait(false);
                return null;
            case LatencyFault latency:
                await DelayAsync(latency.Delay, cancellationToken).ConfigureAwait(false);
                return null;
            case JitterFault jitter:
                await DelayAsync(NextJitter(jitter), cancellationToken).ConfigureAwait(false);
                return null;
            case FailFault fail:
                var exception = fail.CreateException(call);
                _injected.AddOrUpdate(exception, fail);
                throw exception;
            default:
                return decision.Fault;
        }
    }

    /// <summary>The blocking form of <see cref="BeforeCallAsync"/>, for veneers over synchronous APIs.</summary>
    public Fault? BeforeCall(ChaosCall call, CancellationToken cancellationToken = default)
    {
        var pending = BeforeCallAsync(call, cancellationToken);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Records that a call reached the real client and succeeded.</summary>
    public void CallSucceeded(ChaosCall call) => Record(ChaosEventKind.CallSucceeded, call, null);

    /// <summary>
    /// Records that a call reached the real client and the real client threw.
    /// Exceptions that the engine injected itself are ignored, so wrappers can report every failure.
    /// </summary>
    public void CallFailed(ChaosCall call, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!_injected.TryGetValue(exception, out _))
        {
            Record(ChaosEventKind.CallFailed, call, exception);
        }
    }

    /// <summary>Runs a synchronous real call through the timeline.</summary>
    public void Run(ChaosCall call, Action realCall)
    {
        ArgumentNullException.ThrowIfNull(realCall);
        Run(call, () =>
        {
            realCall();
            return true;
        });
    }

    /// <summary>Runs a synchronous real call through the timeline and returns its result.</summary>
    public T Run<T>(ChaosCall call, Func<T> realCall)
    {
        ArgumentNullException.ThrowIfNull(realCall);
        var fault = BeforeCall(call);
        if (fault is not null)
        {
            throw Unsupported(fault);
        }

        try
        {
            var result = realCall();
            CallSucceeded(call);
            return result;
        }
        catch (Exception ex)
        {
            CallFailed(call, ex);
            throw;
        }
    }

    /// <summary>Runs an asynchronous real call through the timeline.</summary>
    public async Task RunAsync(ChaosCall call, Func<Task> realCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realCall);
        await RunAsync(call, async () =>
        {
            await realCall().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an asynchronous real call through the timeline and returns its result.</summary>
    public async Task<T> RunAsync<T>(ChaosCall call, Func<Task<T>> realCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realCall);
        var fault = await BeforeCallAsync(call, cancellationToken).ConfigureAwait(false);
        if (fault is not null)
        {
            throw Unsupported(fault);
        }

        try
        {
            var result = await realCall().ConfigureAwait(false);
            CallSucceeded(call);
            return result;
        }
        catch (Exception ex)
        {
            CallFailed(call, ex);
            throw;
        }
    }

    private static NotSupportedException Unsupported(Fault fault) =>
        new($"The fault '{fault.Name}' is not supported by this veneer.");

    private void Record(ChaosEventKind kind, ChaosCall call, Exception? exception)
    {
        lock (_gate)
        {
            var now = Clock.GetUtcNow();
            if (_startedAt is not null)
            {
                Advance(now);
            }

            _log.Add(new ChaosEvent(now, kind, Operation: call.Operation, Exception: exception));
        }
    }

    private Decision Decide(ChaosCall call)
    {
        if (!_started)
        {
            Start();
        }

        lock (_gate)
        {
            var now = Clock.GetUtcNow();
            Advance(now);

            if (_index >= _segments.Count)
            {
                return default;
            }

            var segment = _segments[_index];
            DateTimeOffset? activeUntil;
            switch (segment.Kind)
            {
                case SegmentKind.Gap:
                    return default;
                case SegmentKind.Repeating:
                    var offset = TimeSpan.FromTicks((now - _segmentStart).Ticks % segment.Period.Ticks);
                    if (offset >= segment.Duration)
                    {
                        return default;
                    }

                    activeUntil = now + (segment.Duration - offset);
                    break;
                default:
                    activeUntil = segment.End == WindowEnd.Duration ? _segmentStart + segment.Duration : null;
                    break;
            }

            if ((segment.When is not null && !segment.When(call)) || !segment.Fault!.AppliesTo(call))
            {
                return default;
            }

            _segmentCalls++;

            if (segment.Rate < 1.0 && _random.NextDouble() >= segment.Rate)
            {
                _log.Add(new ChaosEvent(now, ChaosEventKind.FaultSkipped, segment.WindowIndex, call.Operation));
                return default;
            }

            _log.Add(new ChaosEvent(now, ChaosEventKind.FaultInjected, segment.WindowIndex, call.Operation, segment.Fault!.Name)
            {
                WindowEndsAt = activeUntil,
            });

            return new Decision(segment.Fault, segment, activeUntil);
        }
    }

    private void Advance(DateTimeOffset now)
    {
        while (_index < _segments.Count)
        {
            var segment = _segments[_index];
            DateTimeOffset endedAt;

            if (segment.Kind == SegmentKind.Repeating)
            {
                return;
            }

            switch (segment.End)
            {
                case WindowEnd.Duration when now >= _segmentStart + segment.Duration:
                    endedAt = _segmentStart + segment.Duration;
                    break;
                case WindowEnd.Calls when _segmentCalls >= segment.Calls:
                    endedAt = now;
                    break;
                case WindowEnd.Until when segment.Until!():
                    endedAt = now;
                    break;
                default:
                    return;
            }

            if (segment.Kind == SegmentKind.Window)
            {
                _log.Add(new ChaosEvent(endedAt, ChaosEventKind.WindowEnded, segment.WindowIndex));
            }

            _index++;
            _segmentStart = endedAt;
            _segmentCalls = 0;
            LogSegmentStarted(endedAt);
        }
    }

    private void LogSegmentStarted(DateTimeOffset at)
    {
        if (_index < _segments.Count && _segments[_index].Kind != SegmentKind.Gap)
        {
            _log.Add(new ChaosEvent(at, ChaosEventKind.WindowStarted, _segments[_index].WindowIndex));
        }
    }

    private async ValueTask FreezeAsync(Decision decision, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _frozen);
        try
        {
            if (decision.ActiveUntil is { } until)
            {
                var wait = until - Clock.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, Clock, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var deadline = Clock.GetUtcNow() + MaxFreeze;
            if (decision.Segment!.End == WindowEnd.Until)
            {
                while (!WindowHasEnded(decision.Segment))
                {
                    if (Clock.GetUtcNow() >= deadline)
                    {
                        throw new ChaosFreezeTimeoutException(MaxFreeze);
                    }

                    await Task.Delay(PollInterval, Clock, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await Task.Delay(MaxFreeze, Clock, cancellationToken).ConfigureAwait(false);
            throw new ChaosFreezeTimeoutException(MaxFreeze);
        }
        finally
        {
            Interlocked.Decrement(ref _frozen);
        }
    }

    private bool WindowHasEnded(Segment segment)
    {
        lock (_gate)
        {
            Advance(Clock.GetUtcNow());
            var position = _segments.IndexOf(segment);
            return _index > position;
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, Clock, cancellationToken);

    private TimeSpan NextJitter(JitterFault jitter)
    {
        lock (_gate)
        {
            var span = (jitter.Max - jitter.Min).Ticks;
            return jitter.Min + TimeSpan.FromTicks((long)(_random.NextDouble() * span));
        }
    }

    private readonly record struct Decision(Fault? Fault, Segment? Segment, DateTimeOffset? ActiveUntil);
}

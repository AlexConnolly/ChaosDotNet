using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace ChaosDotNet;

/// <summary>
/// Wraps a <see cref="FakeTimeProvider"/> and remembers when each of its timers is next due, so
/// <see cref="ChaosMonkey.RunAsync(Func{Task}, double)"/> can move time straight to the next timer instead of stepping blindly.
/// </summary>
internal sealed class ChaosClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<TrackedTimer> _timers = [];
    private readonly ConcurrentQueue<Exception> _errors = new();
    private int _created;

    public ChaosClock(FakeTimeProvider inner)
    {
        Inner = inner;
    }

    public FakeTimeProvider Inner { get; }

    /// <summary>How many timers have been created so far. A new timer means the workload has moved on and is waiting again.</summary>
    public int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _created;
            }
        }
    }

    public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();

    public override long GetTimestamp() => Inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var tracked = new TrackedTimer(this);
        lock (_gate)
        {
            // Callbacks run on the thread pool, never inline inside Advance: a workload continuation that blocks
            // (for example a synchronous call that freezes) must not block the thread that moves time forward.
            tracked.Inner = Inner.CreateTimer(
                s =>
                {
                    tracked.Fired();
                    ThreadPool.UnsafeQueueUserWorkItem(
                        static state =>
                        {
                            try
                            {
                                state.callback(state.s);
                            }
                            catch (Exception ex)
                            {
                                state.errors.Enqueue(ex);
                            }
                        },
                        (callback, s, errors: _errors),
                        preferLocal: false);
                },
                state,
                dueTime,
                period);
            tracked.Schedule(dueTime, period);
            _created++;
        }

        return tracked;
    }

    public DateTimeOffset? NextDue()
    {
        lock (_gate)
        {
            DateTimeOffset? next = null;
            foreach (var timer in _timers)
            {
                if (timer.Due is { } due && (next is null || due < next))
                {
                    next = due;
                }
            }

            return next;
        }
    }

    /// <summary>Moves time forward. Holds the same lock as timer creation, so a timer's recorded due time always matches the real one.</summary>
    /// <summary>Takes an exception thrown by a timer callback, so the monkey's run fails with it instead of the process crashing.</summary>
    public bool TryTakeError(out Exception error) => _errors.TryDequeue(out error!);

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            Inner.Advance(by);
        }
    }

    private void Track(TrackedTimer timer, bool active)
    {
        lock (_gate)
        {
            if (active)
            {
                _timers.Add(timer);
            }
            else
            {
                _timers.Remove(timer);
            }
        }
    }

    private sealed class TrackedTimer(ChaosClock clock) : ITimer
    {
        private TimeSpan _period;

        public ITimer Inner { get; set; } = null!;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                var changed = Inner.Change(dueTime, period);
                Schedule(dueTime, period);
                return changed;
            }
        }

        public void Dispose()
        {
            clock.Track(this, active: false);
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            clock.Track(this, active: false);
            return Inner.DisposeAsync();
        }

        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
            clock.Track(this, Due is not null);
        }

        public void Fired()
        {
            var repeats = _period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero;
            Due = repeats ? clock.GetUtcNow() + _period : null;
            clock.Track(this, repeats);
        }
    }
}

namespace ChaosDotNet;

/// <summary>
/// Runs several factories on one clock. All factories built with the same scenario start their
/// timelines at the same moment: on <see cref="Start"/>, or on the first <c>Create...</c> call of any of them.
/// </summary>
public class ChaosScenario
{
    private readonly object _gate = new();
    private readonly List<ChaosEngine> _engines = [];
    private DateTimeOffset? _startedAt;
    private int _nextSeed;

    /// <summary>Creates a scenario.</summary>
    /// <param name="clock">The clock for all factories in the scenario. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="seed">The base seed. Each factory gets its own seed derived from it.</param>
    public ChaosScenario(TimeProvider? clock = null, int? seed = null)
    {
        Clock = clock ?? TimeProvider.System;
        Seed = seed ?? Random.Shared.Next();
        _nextSeed = Seed;
    }

    /// <summary>The clock shared by all factories in the scenario.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The base seed.</summary>
    public int Seed { get; }

    /// <summary>When the scenario started, or <see langword="null"/> before it starts.</summary>
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

    /// <summary>Starts every factory's timeline now. Does nothing if the scenario has already started.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_startedAt is not null)
            {
                return;
            }

            var at = Clock.GetUtcNow();
            OnStarting(_engines);
            foreach (var engine in _engines)
            {
                engine.StartAt(at);
            }

            _startedAt = at;
        }
    }

    /// <summary>The engines registered so far, in registration order.</summary>
    internal IReadOnlyList<ChaosEngine> Engines
    {
        get
        {
            lock (_gate)
            {
                return _engines.ToArray();
            }
        }
    }

    private protected virtual void OnStarting(IReadOnlyList<ChaosEngine> engines)
    {
    }

    internal int NextSeed()
    {
        lock (_gate)
        {
            return unchecked(_nextSeed++);
        }
    }

    internal void Register(ChaosEngine engine)
    {
        lock (_gate)
        {
            _engines.Add(engine);
            engine.Name = $"dependency{_engines.Count}";
            if (_startedAt is { } at)
            {
                engine.StartAt(at);
            }
        }
    }
}

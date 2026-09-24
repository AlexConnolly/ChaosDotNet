using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;

namespace ChaosDotNet;

/// <summary>
/// Connects every factory built with it and breaks them at random, on a plan decided by a seed.
/// The same seed always gives the same plan, so a failure can be replayed.
/// </summary>
/// <example>
/// <code>
/// var monkey = new ChaosMonkey();
/// var orders = new SqlFactory(monkey).Named("orders");
/// var payments = new HttpFactory(monkey).Named("payments");
/// // ... build the app with veneers from both ...
/// await monkey.RunAsync(() => PlaceOrders(app));
/// </code>
/// </example>
public sealed class ChaosMonkey : ChaosScenario
{
    private readonly IReadOnlySet<int>? _only;
    private ChaosPlan? _plan;

    /// <summary>Creates a monkey.</summary>
    /// <param name="clock">The clock for every factory. Defaults to <see cref="TimeProvider.System"/>. Pass a <see cref="FakeTimeProvider"/> to run plans in milliseconds with <see cref="RunAsync(Func{Task}, double)"/>.</param>
    /// <param name="seed">The seed. Defaults to the <c>CHAOS_SEED</c> environment variable, then a random seed.</param>
    /// <param name="options">The settings.</param>
    public ChaosMonkey(TimeProvider? clock = null, int? seed = null, ChaosMonkeyOptions? options = null)
        : this(clock, seed ?? SeedFromEnvironment(), options, IncidentsFromEnvironment())
    {
    }

    internal ChaosMonkey(TimeProvider? clock, int? seed, ChaosMonkeyOptions? options, IReadOnlySet<int>? only)
        : base(clock is FakeTimeProvider fake ? new ChaosClock(fake) : clock, seed)
    {
        Options = options ?? new ChaosMonkeyOptions();
        _only = only;
        if (Options.Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The plan duration must be more than zero.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaxConcurrentIncidents, nameof(options));
    }

    /// <summary>The settings.</summary>
    public ChaosMonkeyOptions Options { get; }

    /// <summary>
    /// Creates a monkey that replays the plan for <paramref name="seed"/> with only the given incident numbers. Used to shrink
    /// a failing plan, and to replay a shrunk plan from a failure message.
    /// </summary>
    public static ChaosMonkey Replay(int seed, IEnumerable<int> incidents, TimeProvider? clock = null, ChaosMonkeyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        return new ChaosMonkey(clock, seed, options, incidents.ToHashSet());
    }

    /// <summary>The plan. It is built when the monkey starts, from every factory registered by then.</summary>
    public ChaosPlan Plan => _plan ?? new ChaosPlan(Seed, [], _only);

    /// <summary>
    /// Starts the monkey, runs the workload, and waits for it. With a <see cref="FakeTimeProvider"/> clock, time moves like a
    /// simulation: when the workload has settled, the clock jumps to the next timer it waits on. The same seed then gives the same
    /// history on any machine, and a whole plan plays out in a fraction of a second. Pace the workload on <see cref="ChaosScenario.Clock"/>.
    /// </summary>
    /// <param name="workload">The code to run under chaos.</param>
    /// <param name="stepMilliseconds">How far a fake clock moves when the workload waits on nothing the monkey can see. Defaults to 100 ms.</param>
    public async Task RunAsync(Func<Task> workload, double stepMilliseconds = 100)
    {
        ArgumentNullException.ThrowIfNull(workload);
        await RunAsync(async () =>
        {
            await workload().ConfigureAwait(false);
            return true;
        }, stepMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the monkey, runs the workload, and returns its result. With a <see cref="FakeTimeProvider"/> clock, time moves like a
    /// simulation: when the workload has settled, the clock jumps to the next timer it waits on. The same seed then gives the same
    /// history on any machine, and a whole plan plays out in a fraction of a second. Pace the workload on <see cref="ChaosScenario.Clock"/>.
    /// </summary>
    /// <param name="workload">The code to run under chaos.</param>
    /// <param name="stepMilliseconds">How far a fake clock moves when the workload waits on nothing the monkey can see. Defaults to 100 ms.</param>
    public async Task<T> RunAsync<T>(Func<Task<T>> workload, double stepMilliseconds = 100)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepMilliseconds);

        Start();
        var run = Task.Run(workload);
        if (Clock is ChaosClock clock)
        {
            await PlayAsync(clock, run, TimeSpan.FromMilliseconds(stepMilliseconds)).ConfigureAwait(false);
        }

        try
        {
            return await run.WaitAsync(Options.RunTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!run.IsCompleted)
        {
            throw new TimeoutException($"The workload did not finish within {Options.RunTimeout} of real time.{Environment.NewLine}{Plan}");
        }
    }

    /// <summary>
    /// Runs a scenario once per seed, each time with a new monkey. Throws <see cref="ChaosExplorationException"/> listing every
    /// failing seed with its plan, shrunk to the fewest incidents that still fail.
    /// </summary>
    /// <param name="runs">How many seeds to try. By default the seeds are 1 to <paramref name="runs"/>, so results are the same on every build.</param>
    /// <param name="scenario">The test. Build factories with the monkey it gets, run the workload with <see cref="RunAsync(Func{Task}, double)"/>, and assert invariants.</param>
    /// <param name="options">The settings.</param>
    public static Task ExploreAsync(int runs, Func<ChaosMonkey, Task> scenario, ChaosExploreOptions? options = null)
    {
        options ??= new ChaosExploreOptions();
        return ChaosExplorer.ExploreAsync(runs, scenario, options, options.Name ?? CallerName());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallerName()
    {
        foreach (var frame in new StackTrace().GetFrames().Skip(1))
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null || type.Assembly == typeof(ChaosMonkey).Assembly)
            {
                continue;
            }

            // Async methods and lambdas run on compiler-generated types such as <Method>d__4 and <>c, with names like <Method>b__4_0.
            if (type.Name.StartsWith('<'))
            {
                var owner = type;
                while (owner.DeclaringType is not null && owner.Name.StartsWith('<'))
                {
                    owner = owner.DeclaringType;
                }

                var name = Unmangle(type.Name);
                return $"{owner.Name}.{(name.Length > 0 ? name : Unmangle(method.Name))}";
            }

            return $"{type.Name}.{Unmangle(method.Name)}";
        }

        return "Explore";

        static string Unmangle(string name) =>
            name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is var end and > 0 ? name[1..end] : name;
    }

    /// <summary>
    /// Moves a fake clock like a simulation: wait until the workload has settled, then jump to the next timer it waits on.
    /// Time only moves while the workload waits, so the same seed gives the same history on a fast or a slow machine.
    /// </summary>
    private async Task PlayAsync(ChaosClock clock, Task run, TimeSpan fallbackStep)
    {
        var timer = Stopwatch.StartNew();
        var created = 0;
        var untrackedRounds = 0;
        while (!run.IsCompleted)
        {
            // After a few rounds with no new tracked timer, the workload is not waiting on monkey.Clock: stop waiting for one.
            await SettleAsync(run, clock, created, untrackedRounds > 3 ? 1 : 50).ConfigureAwait(false);
            ThrowTimerError(clock);
            untrackedRounds = clock.TimersCreated == created ? untrackedRounds + 1 : 0;
            created = clock.TimersCreated;
            if (run.IsCompleted)
            {
                break;
            }

            if (timer.Elapsed > Options.RunTimeout)
            {
                throw new TimeoutException($"The workload did not finish within {Options.RunTimeout} of real time.{Environment.NewLine}{Plan}");
            }

            if (clock.NextDue() is { } due)
            {
                var wait = due - clock.GetUtcNow();
                clock.Advance(wait > TimeSpan.Zero ? wait : TimeSpan.FromTicks(1));
                continue;
            }

            // No timer to jump to: the workload is doing real I/O, or waits on a clock other than monkey.Clock.
            await Task.Yield();
            if (!run.IsCompleted && clock.NextDue() is null)
            {
                clock.Advance(fallbackStep);
            }
        }
    }

    private static void ThrowTimerError(ChaosClock clock)
    {
        if (clock.TryTakeError(out var error))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>
    /// Waits until the workload has settled: it has created a new timer since the last move (it is waiting again) and no
    /// thread-pool work is queued. Gives up after <paramref name="limitMilliseconds"/> of real time, for workloads that wait on something else.
    /// </summary>
    private static async Task SettleAsync(Task run, ChaosClock clock, int createdBefore, int limitMilliseconds)
    {
        var limit = Stopwatch.StartNew();
        while (!run.IsCompleted && limit.ElapsedMilliseconds < limitMilliseconds)
        {
            if (clock.TimersCreated > createdBefore && ThreadPool.PendingWorkItemCount == 0)
            {
                await Task.Yield();
                if (ThreadPool.PendingWorkItemCount == 0)
                {
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private protected override void OnStarting(IReadOnlyList<ChaosEngine> engines)
    {
        var targets = engines
            .Where(e => !e.HasSegments)
            .Select(e => (Engine: e, Faults: e.MonkeyFaults().Where(f => Options.AllowDataLoss || f.Kind != MonkeyFaultKind.DataLoss).ToArray()))
            .Where(t => t.Faults.Length > 0)
            .ToList();

        var duplicate = targets.GroupBy(t => t.Engine.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two dependencies are named '{duplicate.Key}'. Give each factory its own name with Named(...).");
        }

        var planned = PlanBuilder.Build(Seed, Options, targets.Select(t => (t.Engine.Name, (IReadOnlyList<MonkeyFault>)t.Faults)).ToList());
        var incidents = planned.Where(p => _only is null || _only.Contains(p.Incident.Number)).ToList();
        _plan = new ChaosPlan(Seed, incidents.Select(p => p.Incident).ToList(), _only);

        foreach (var (engine, _) in targets)
        {
            var cursor = TimeSpan.Zero;
            foreach (var (incident, faults, rateSeed) in incidents.Where(p => p.Faults.ContainsKey(engine.Name)))
            {
                if (incident.Start > cursor)
                {
                    engine.AddSegment(Segment.Gap(incident.Start - cursor));
                }

                engine.AddSegment(new Segment
                {
                    Kind = SegmentKind.Window,
                    End = WindowEnd.Duration,
                    Duration = incident.Length,
                    Fault = faults[engine.Name],
                    Rate = incident.Rate,
                    Random = new Random(rateSeed ^ StableHash(engine.Name)),
                });
                cursor = incident.End;
            }
        }
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }

    private static int? SeedFromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) ? seed : null;

    private static HashSet<int>? IncidentsFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("CHAOS_INCIDENTS");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => int.Parse(n, CultureInfo.InvariantCulture))
            .ToHashSet();
    }
}

internal static class PlanBuilder
{
    public static List<(ChaosIncident Incident, Dictionary<string, Fault> Faults, int RateSeed)> Build(
        int seed,
        ChaosMonkeyOptions options,
        IReadOnlyList<(string Name, IReadOnlyList<MonkeyFault> Faults)> targets)
    {
        var random = new Random(seed);
        var planned = new List<(ChaosIncident Incident, Dictionary<string, Fault> Faults, int RateSeed)>();
        if (targets.Count == 0)
        {
            return planned;
        }

        var (minGap, maxGap, maxLength) = options.Intensity switch
        {
            ChaosIntensity.Low => (8.0, 20.0, 4.0),
            ChaosIntensity.High => (0.5, 4.0, 12.0),
            _ => (3.0, 10.0, 8.0),
        };

        var duration = options.Duration.TotalSeconds;
        var time = Between(random, 0, maxGap);
        while (time < duration)
        {
            time = Math.Round(time, 1);
            var length = Math.Round(Math.Min(Between(random, 1, maxLength), duration - time), 1);
            if (length <= 0)
            {
                break;
            }

            var active = planned.Where(p => p.Incident.Start.TotalSeconds < time + length && p.Incident.End.TotalSeconds > time).ToList();
            var busy = active.SelectMany(p => p.Faults.Keys).ToHashSet();
            var free = targets.Where(t => !busy.Contains(t.Name)).ToList();

            if (active.Count >= options.MaxConcurrentIncidents || free.Count == 0)
            {
                time = active.Min(p => p.Incident.End.TotalSeconds) + 0.1;
                continue;
            }

            var count = options.CorrelatedFaults && free.Count >= 2 && random.NextDouble() < 0.25
                ? random.Next(2, Math.Min(free.Count, 3) + 1)
                : 1;
            var chosen = free.OrderBy(_ => random.Next()).Take(count).ToList();
            MonkeyFaultKind? kind = count > 1 ? (random.NextDouble() < 0.7 ? MonkeyFaultKind.Outage : MonkeyFaultKind.Slowness) : null;

            var faults = new Dictionary<string, Fault>();
            var described = new List<ChaosIncidentFault>();
            foreach (var target in chosen)
            {
                var candidates = kind is null ? target.Faults : target.Faults.Where(f => f.Kind == kind).ToList();
                if (candidates.Count == 0)
                {
                    candidates = target.Faults;
                }

                var entry = PickWeighted(random, candidates);
                faults[target.Name] = entry.Create(new Random(random.Next()));
                described.Add(new ChaosIncidentFault(target.Name, entry.Name, entry.Kind));
            }

            var rate = random.NextDouble() < 0.5 ? 1.0 : Math.Round(0.2 + (random.NextDouble() * 0.6), 2);
            var incident = new ChaosIncident(planned.Count + 1, TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(length), described, rate);
            planned.Add((incident, faults, random.Next()));
            time += length + Between(random, minGap, maxGap);
        }

        return planned;
    }

    private static double Between(Random random, double min, double max) => min + (random.NextDouble() * (max - min));

    private static MonkeyFault PickWeighted(Random random, IReadOnlyList<MonkeyFault> faults)
    {
        var roll = random.NextDouble() * faults.Sum(f => f.Weight);
        foreach (var fault in faults)
        {
            roll -= fault.Weight;
            if (roll < 0)
            {
                return fault;
            }
        }

        return faults[^1];
    }
}

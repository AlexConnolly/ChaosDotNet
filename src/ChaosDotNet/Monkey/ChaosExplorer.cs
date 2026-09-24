using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ChaosDotNet;

/// <summary>Settings for <see cref="ChaosMonkey.ExploreAsync"/>.</summary>
public sealed class ChaosExploreOptions
{
    /// <summary>The settings for each run's monkey.</summary>
    public ChaosMonkeyOptions Monkey { get; init; } = new();

    /// <summary>Exact seeds to try instead of 1 to N.</summary>
    public IReadOnlyList<int>? Seeds { get; init; }

    /// <summary>Try new random seeds each time instead of 1 to N. The <c>CHAOS_EXPLORE=random</c> environment variable does the same.</summary>
    public bool RandomSeeds { get; init; }

    /// <summary>Whether to shrink each failing plan to the fewest incidents that still fail. Defaults to <see langword="true"/>.</summary>
    public bool Shrink { get; init; } = true;

    /// <summary>The most extra runs spent shrinking one failing seed. Defaults to 30.</summary>
    public int MaxShrinkRuns { get; init; } = 30;

    /// <summary>
    /// Use the system clock instead of a new <see cref="FakeTimeProvider"/> for each run. Use this against real
    /// containers when the plan must play out in real time.
    /// </summary>
    public bool RealTime { get; init; }
}

/// <summary>One failing seed found by <see cref="ChaosMonkey.ExploreAsync"/>.</summary>
/// <param name="Seed">The seed.</param>
/// <param name="Plan">The full plan for the seed.</param>
/// <param name="ShrunkPlan">The smallest plan found that still fails.</param>
/// <param name="Exception">The failure from the smallest plan.</param>
public sealed record ChaosExplorationFailure(int Seed, ChaosPlan Plan, ChaosPlan ShrunkPlan, Exception Exception);

/// <summary>Thrown by <see cref="ChaosMonkey.ExploreAsync"/> when one or more seeds fail.</summary>
public sealed class ChaosExplorationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChaosExplorationException(IReadOnlyList<ChaosExplorationFailure> failures, int runs)
        : base(Describe(failures, runs), failures.Count > 0 ? failures[0].Exception : null)
    {
        Failures = failures;
        Runs = runs;
    }

    /// <summary>Every failing seed.</summary>
    public IReadOnlyList<ChaosExplorationFailure> Failures { get; }

    /// <summary>How many seeds were tried.</summary>
    public int Runs { get; }

    private static string Describe(IReadOnlyList<ChaosExplorationFailure> failures, int runs)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Chaos exploration failed on {failures.Count} of {runs} seed(s): {string.Join(", ", failures.Select(f => f.Seed))}.");
        foreach (var failure in failures)
        {
            text.AppendLine().AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"Seed {failure.Seed}: {failure.Exception.GetType().Name}: {failure.Exception.Message}");
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"Smallest failing plan ({failure.ShrunkPlan.Incidents.Count} of {failure.Plan.Incidents.Count} incident(s)):");
            if (failure.ShrunkPlan.Incidents.Count == 0)
            {
                text.AppendLine().Append("  (no incidents: the test fails without chaos)");
            }

            foreach (var incident in failure.ShrunkPlan.Incidents)
            {
                text.AppendLine().Append("  ").Append(incident);
            }

            text.AppendLine().Append("Reproduce: set ").Append(failure.ShrunkPlan.ReproduceWith);
        }

        return text.ToString();
    }
}

internal static class ChaosExplorer
{
    public static async Task ExploreAsync(int runs, Func<ChaosMonkey, Task> scenario, ChaosExploreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runs);
        ArgumentNullException.ThrowIfNull(scenario);

        var (seeds, only) = ChooseSeeds(runs, options);
        var failures = new List<ChaosExplorationFailure>();

        foreach (var seed in seeds)
        {
            var (error, plan) = await RunOnceAsync(seed, only, scenario, options).ConfigureAwait(false);
            if (error is null)
            {
                continue;
            }

            var (shrunk, shrunkError) = options.Shrink
                ? await ShrinkAsync(seed, plan, error, scenario, options).ConfigureAwait(false)
                : (plan, error);
            failures.Add(new ChaosExplorationFailure(seed, plan, shrunk, shrunkError));
        }

        if (failures.Count > 0)
        {
            throw new ChaosExplorationException(failures, seeds.Count);
        }
    }

    private static (List<int> Seeds, IReadOnlySet<int>? Only) ChooseSeeds(int runs, ChaosExploreOptions options)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
        {
            var incidents = Environment.GetEnvironmentVariable("CHAOS_INCIDENTS");
            IReadOnlySet<int>? only = string.IsNullOrWhiteSpace(incidents)
                ? null
                : incidents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToHashSet();
            return ([single], only);
        }

        if (options.Seeds is { Count: > 0 } seeds)
        {
            return ([.. seeds], null);
        }

        var random = options.RandomSeeds || string.Equals(Environment.GetEnvironmentVariable("CHAOS_EXPLORE"), "random", StringComparison.OrdinalIgnoreCase);
        return (random ? Enumerable.Range(0, runs).Select(_ => Random.Shared.Next()).ToList() : Enumerable.Range(1, runs).ToList(), null);
    }

    private static async Task<(Exception? Error, ChaosPlan Plan)> RunOnceAsync(int seed, IReadOnlySet<int>? only, Func<ChaosMonkey, Task> scenario, ChaosExploreOptions options)
    {
        var clock = options.RealTime ? TimeProvider.System : new FakeTimeProvider();
        var monkey = new ChaosMonkey(clock, seed, options.Monkey, only);
        try
        {
            await scenario(monkey).ConfigureAwait(false);
            return (null, monkey.Plan);
        }
        catch (Exception ex)
        {
            return (ex, monkey.Plan);
        }
    }

    private static async Task<(ChaosPlan Plan, Exception Error)> ShrinkAsync(int seed, ChaosPlan plan, Exception error, Func<ChaosMonkey, Task> scenario, ChaosExploreOptions options)
    {
        var kept = plan.Incidents.Select(i => i.Number).ToHashSet();
        var smallest = plan;
        var budget = options.MaxShrinkRuns;

        foreach (var number in plan.Incidents.Select(i => i.Number).ToList())
        {
            if (budget-- <= 0)
            {
                break;
            }

            var trial = kept.Where(n => n != number).ToHashSet();
            var (trialError, trialPlan) = await RunOnceAsync(seed, trial, scenario, options).ConfigureAwait(false);
            if (trialError is not null)
            {
                kept = trial;
                smallest = trialPlan;
                error = trialError;
            }
        }

        return (smallest, error);
    }
}

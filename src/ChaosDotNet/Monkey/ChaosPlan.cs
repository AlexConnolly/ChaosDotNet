using System.Globalization;
using System.Text;

namespace ChaosDotNet;

/// <summary>How much chaos a <see cref="ChaosMonkey"/> causes.</summary>
public enum ChaosIntensity
{
    /// <summary>Short incidents, 8 to 20 seconds apart.</summary>
    Low,

    /// <summary>Incidents up to 8 seconds long, 3 to 10 seconds apart.</summary>
    Medium,

    /// <summary>Incidents up to 12 seconds long, 0.5 to 4 seconds apart.</summary>
    High,
}

/// <summary>Settings for a <see cref="ChaosMonkey"/>.</summary>
public sealed class ChaosMonkeyOptions
{
    /// <summary>How long the plan lasts. After it, calls pass through. Defaults to 60 seconds.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How much chaos to cause. Defaults to <see cref="ChaosIntensity.Medium"/>.</summary>
    public ChaosIntensity Intensity { get; init; } = ChaosIntensity.Medium;

    /// <summary>Whether some incidents hit several dependencies at once, as a network blip would. Defaults to <see langword="true"/>.</summary>
    public bool CorrelatedFaults { get; init; } = true;

    /// <summary>The most incidents active at the same time. Defaults to 2.</summary>
    public int MaxConcurrentIncidents { get; init; } = 2;

    /// <summary>Whether the monkey may use <see cref="MonkeyFaultKind.DataLoss"/> faults such as dropped messages. Defaults to <see langword="false"/>.</summary>
    public bool AllowDataLoss { get; init; }

    /// <summary>How long <see cref="ChaosMonkey.RunAsync(Func{Task}, double)"/> waits in real time for the workload before it gives up. Defaults to 2 minutes.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>One dependency's fault in a <see cref="ChaosIncident"/>.</summary>
/// <param name="Dependency">The dependency's name.</param>
/// <param name="Fault">The catalogue entry's name.</param>
/// <param name="Kind">The kind of harm.</param>
public sealed record ChaosIncidentFault(string Dependency, string Fault, MonkeyFaultKind Kind);

/// <summary>A fault on one or more dependencies for a length of time, then recovery.</summary>
/// <param name="Number">The incident's number in the plan, from 1.</param>
/// <param name="Start">When it starts, from the start of the plan.</param>
/// <param name="Length">How long it lasts.</param>
/// <param name="Faults">The fault on each affected dependency.</param>
/// <param name="Rate">The proportion of calls affected, from 0 to 1.</param>
public sealed record ChaosIncident(int Number, TimeSpan Start, TimeSpan Length, IReadOnlyList<ChaosIncidentFault> Faults, double Rate)
{
    /// <summary>When it ends, from the start of the plan.</summary>
    public TimeSpan End => Start + Length;

    /// <inheritdoc />
    public override string ToString()
    {
        var rate = Rate < 1 ? string.Create(CultureInfo.InvariantCulture, $" ({Rate:P0} of calls)") : string.Empty;
        var faults = string.Join(" + ", Faults.Select(f => $"{f.Dependency} {f.Kind}: {f.Fault}"));
        return $"#{Number} {Format(Start)}-{Format(End)}  {faults}{rate}";
    }

    private static string Format(TimeSpan time) => time.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
}

/// <summary>The incidents a <see cref="ChaosMonkey"/> causes in one run.</summary>
public sealed class ChaosPlan
{
    internal ChaosPlan(int seed, IReadOnlyList<ChaosIncident> incidents, IReadOnlySet<int>? only)
    {
        Seed = seed;
        Incidents = incidents;
        Only = only;
    }

    /// <summary>The seed the plan was built from.</summary>
    public int Seed { get; }

    /// <summary>The incidents in the plan, in start order.</summary>
    public IReadOnlyList<ChaosIncident> Incidents { get; }

    internal IReadOnlySet<int>? Only { get; }

    /// <summary>The environment variables that replay this exact plan.</summary>
    public string ReproduceWith =>
        Only is null
            ? $"CHAOS_SEED={Seed}"
            : $"CHAOS_SEED={Seed} CHAOS_INCIDENTS={string.Join(',', Only.Order())}";

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder($"Plan for seed {Seed}: {Incidents.Count} incident(s)");
        foreach (var incident in Incidents)
        {
            text.AppendLine().Append("  ").Append(incident);
        }

        return text.ToString();
    }
}

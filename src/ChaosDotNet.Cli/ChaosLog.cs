using System.Globalization;
using System.Text.Json;

namespace ChaosDotNet.Cli;

/// <summary>An incident from the monkey's plan, at wall-clock times.</summary>
internal sealed record LoggedIncident(int Number, DateTimeOffset Start, DateTimeOffset End, double Rate, IReadOnlyList<ChaosIncidentFault> Faults);

/// <summary>One call the monkey broke.</summary>
internal sealed record LoggedFault(DateTimeOffset At, string Dependency, string Fault, string? Operation, string? Details);

/// <summary>
/// What the monkey did in one test process: written by the startup hook when the process exits, read by the runner
/// to explain each failure.
/// </summary>
internal sealed record ChaosLog(IReadOnlyList<LoggedIncident> Incidents, IReadOnlyList<LoggedFault> Faults)
{
    public const string Extension = ".chaos.json";

    public static ChaosLog Read(string folder) =>
        Directory.EnumerateFiles(folder, "*" + Extension)
            .Select(file => JsonSerializer.Deserialize<ChaosLog>(File.ReadAllText(file))!)
            .Aggregate(new ChaosLog([], []), (all, log) => new ChaosLog([.. all.Incidents, .. log.Incidents], [.. all.Faults, .. log.Faults]));

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    public string Totals() =>
        Faults.Count == 0
            ? "none"
            : string.Join(", ", Faults.GroupBy(f => f.Dependency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    /// <summary>The test's error, whether an injected fault reached it, and the incidents and broken calls while it ran.</summary>
    public IEnumerable<string> Explain(TestFailure failure)
    {
        yield return $"Error: {failure.Message.Split('\n')[0].Trim()}";
        yield return failure.StackTrace.Contains("ChaosDotNet.Cli.Interceptors", StringComparison.Ordinal)
            ? "Cause: an injected fault reached the test without being handled."
            : "Cause: the test failed on its own assertion or error; a fault below changed what the code did.";

        var incidents = Incidents.Where(i => i.Start < failure.End && i.End > failure.Start).OrderBy(i => i.Start).ToList();
        if (incidents.Count == 0)
        {
            yield return "No incident was active while the test ran. An earlier fault may have left state behind.";
            yield break;
        }

        yield return $"Infra while it ran ({Clock(failure.Start)} to {Clock(failure.End)}):";
        foreach (var incident in incidents)
        {
            var rate = incident.Rate >= 1 ? "all" : $"{incident.Rate * 100:0} % of";
            foreach (var fault in incident.Faults)
            {
                yield return $"  #{incident.Number} {fault.Dependency} {fault.Fault} on {rate} calls, {Clock(incident.Start)} to {Clock(incident.End)}";
            }
        }

        var broken = Faults.Where(f => f.At >= failure.Start && f.At <= failure.End)
            .GroupBy(f => (f.Dependency, Fault: Name(f), f.Operation))
            .OrderBy(g => g.Min(f => f.At))
            .ToList();
        yield return broken.Count == 0 ? "Calls broken: none in this window." : "Calls broken:";
        foreach (var group in broken)
        {
            var example = group.Select(f => f.Details).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            yield return $"  {group.Key.Dependency} {group.Key.Fault} x{group.Count()} on {group.Key.Operation}{(example is null ? "" : $": {example}")}";
        }
    }

    /// <summary>The catalogue name from the incident that broke the call, such as <c>ConnectionFailure (#2)</c>. The engine only knows the fault's type, such as <c>Fail</c>.</summary>
    private string Name(LoggedFault fault) =>
        Incidents
            .Where(i => i.Start <= fault.At && fault.At <= i.End)
            .SelectMany(i => i.Faults.Where(f => f.Dependency == fault.Dependency).Select(f => $"{f.Fault} (#{i.Number})"))
            .FirstOrDefault() ?? fault.Fault;

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

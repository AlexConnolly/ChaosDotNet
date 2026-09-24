namespace ChaosDotNet.Reports;

/// <summary>What coverage keeps of one run. Stored as JSON in the report folder.</summary>
internal sealed class ChaosRunRecord
{
    public string Test { get; set; } = string.Empty;

    public int Seed { get; set; }

    public string? Failure { get; set; }

    public string? Report { get; set; }

    public string? ReproduceWith { get; set; }

    public DateTimeOffset At { get; set; }

    public List<DependencyRecord> Dependencies { get; set; } = [];

    public static ChaosRunRecord From(ChaosScenario scenario, string test, string? failure, string? report, ChaosPlan? shrunkPlan)
    {
        var plan = scenario is ChaosMonkey monkey ? monkey.Plan : null;
        var start = scenario.StartedAt ?? scenario.Clock.GetUtcNow();
        var record = new ChaosRunRecord
        {
            Test = test,
            Seed = scenario.Seed,
            Failure = failure,
            Report = report,
            ReproduceWith = (shrunkPlan ?? plan)?.ReproduceWith,
            At = DateTimeOffset.UtcNow,
        };

        foreach (var group in scenario.Engines.GroupBy(e => e.Name, StringComparer.Ordinal))
        {
            var dependency = new DependencyRecord { Name = group.Key };
            foreach (var entry in group.SelectMany(e => e.MonkeyFaults()).DistinctBy(f => f.Name))
            {
                dependency.Faults.Add(new FaultRecord { Fault = entry.Name, Kind = entry.Kind });
            }

            var injected = group.SelectMany(e => e.Log).Where(e => e.Kind == ChaosEventKind.FaultInjected).Select(e => e.Timestamp - start).ToList();
            foreach (var incident in plan?.Incidents ?? [])
            {
                foreach (var fault in incident.Faults.Where(f => f.Dependency == group.Key))
                {
                    var entry = dependency.Faults.FirstOrDefault(f => f.Fault == fault.Fault);
                    if (entry is null)
                    {
                        entry = new FaultRecord { Fault = fault.Fault, Kind = fault.Kind };
                        dependency.Faults.Add(entry);
                    }

                    entry.Planned++;
                    entry.Calls += injected.Count(at => at >= incident.Start && at < incident.End);
                }
            }

            record.Dependencies.Add(dependency);
        }

        return record;
    }

    internal sealed class DependencyRecord
    {
        public string Name { get; set; } = string.Empty;

        public List<FaultRecord> Faults { get; set; } = [];
    }

    internal sealed class FaultRecord
    {
        public string Fault { get; set; } = string.Empty;

        public MonkeyFaultKind Kind { get; set; }

        public int Planned { get; set; }

        public int Calls { get; set; }
    }
}

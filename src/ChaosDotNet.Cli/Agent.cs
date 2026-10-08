using ChaosDotNet.Factories;

namespace ChaosDotNet.Cli;

/// <summary>Runs in a test process under chaos: starts the monkey, patches the process, and logs what it broke on exit.</summary>
internal static class Agent
{
    public static void Start(int seed)
    {
        var intensity = Enum.Parse<ChaosIntensity>(Environment.GetEnvironmentVariable(Runner.IntensityVariable) ?? nameof(ChaosIntensity.High), ignoreCase: true);
        var monkey = new ChaosMonkey(seed: seed, options: new ChaosMonkeyOptions { Duration = TimeSpan.FromHours(1), Intensity = intensity });
        var http = new HttpFactory(monkey).Named("http");
        var sql = new SqlFactory(monkey).Named("sql");
        monkey.Start();
        Interceptors.Install(http, sql);

        if (Environment.GetEnvironmentVariable(Runner.OutputVariable) is { } output)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                var started = monkey.StartedAt!.Value;
                new ChaosLog(
                    [.. monkey.Plan.Incidents.Select(i => new LoggedIncident(i.Number, started + i.Start, started + i.End, i.Rate, i.Faults))],
                    [.. new[] { http.Engine, sql.Engine }.SelectMany(e => e.Log
                        .Where(l => l.Kind == ChaosEventKind.FaultInjected)
                        .Select(l => new LoggedFault(l.Timestamp, e.Name, l.Fault!, l.Operation, l.Details)))])
                    .Write(Path.Combine(output, Environment.ProcessId + ChaosLog.Extension));
            };
        }
    }
}

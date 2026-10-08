using ChaosDotNet.Factories;

namespace ChaosDotNet.Cli;

/// <summary>Runs in a test process under chaos: starts the monkey, patches the process, and logs what it broke on exit.</summary>
internal static class Agent
{
    public static void Start(int seed)
    {
        var intensity = Enum.Parse<ChaosIntensity>(Environment.GetEnvironmentVariable(Runner.IntensityVariable) ?? nameof(ChaosIntensity.High), ignoreCase: true);
        var options = new ChaosMonkeyOptions { Duration = TimeSpan.FromHours(1), Intensity = intensity };
        var monkey = new ChaosMonkey(seed: seed, options: options);
        var http = new HttpFactory(monkey).Named("http");
        var sql = new SqlFactory(monkey).Named("sql");
        var tcp = new SocketFactory(monkey).Named("tcp");
        monkey.Start();
        Interceptors.Install(http, sql, tcp);
        if (Environment.GetEnvironmentVariable(Runner.ServicesVariable) == "1")
        {
            Interceptors.InstallServices(() => new ChaosMonkey(seed: seed, options: options));
        }

        if (Environment.GetEnvironmentVariable(Runner.OutputVariable) is { } output)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                var monkeys = Interceptors.ServiceMonkeys.Append((monkey, [http.Engine, sql.Engine, tcp.Engine])).ToList();
                new ChaosLog(
                    [.. monkeys.SelectMany(m => m.Monkey.Plan.Incidents.Select(i => new LoggedIncident(i.Number, m.Monkey.StartedAt!.Value + i.Start, m.Monkey.StartedAt!.Value + i.End, i.Rate, i.Faults)))],
                    [.. monkeys.SelectMany(m => m.Engines).SelectMany(e => e.Log
                        .Where(l => l.Kind == ChaosEventKind.FaultInjected)
                        .Select(l => new LoggedFault(l.Timestamp, e.Name, l.Fault!, l.Operation, l.Details)))])
                    .Write(Path.Combine(output, Environment.ProcessId + ChaosLog.Extension));
            };
        }
    }
}

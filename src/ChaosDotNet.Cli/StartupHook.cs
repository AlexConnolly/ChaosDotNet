using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ChaosDotNet;
using ChaosDotNet.Cli;
using ChaosDotNet.Factories;

/// <summary>
/// Loaded into each test process through <c>DOTNET_STARTUP_HOOKS</c> by <c>dotnet chaos test</c>. Starts a monkey with the
/// seed the CLI chose and patches the process so every HTTP and database call goes through it.
/// </summary>
internal static class StartupHook
{
    // Processes that build or orchestrate tests rather than run them.
    private static readonly string[] Tooling = ["dotnet", "MSBuild", "vstest.console", "datacollector", "VBCSCompiler", "dotnet-chaos"];

    public static void Initialize()
    {
        var seed = Environment.GetEnvironmentVariable(Runner.SeedVariable);
        var process = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
        if (seed is null || Tooling.Contains(process, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            Path.Combine(folder, name.Name + ".dll") is var path && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;

        Start(int.Parse(seed, CultureInfo.InvariantCulture));
    }

    // Kept out of Initialize so ChaosDotNet and Harmony load only after the resolver above is in place.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start(int seed)
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

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ChaosDotNet.Cli;

/// <summary>
/// <c>dotnet chaos test</c>: runs <c>dotnet test</c> once without chaos, then once per seed with the startup hook, and
/// reports the tests that passed without chaos but failed with it.
/// </summary>
internal static class Runner
{
    public const string SeedVariable = "CHAOS_CLI_SEED";
    public const string IntensityVariable = "CHAOS_CLI_INTENSITY";
    public const string OutputVariable = "CHAOS_CLI_OUT";

    private const string Usage = """
        Usage: dotnet chaos test [--runs <n>] [--seed <seed>] [--intensity low|medium|high] [dotnet test options]

        Runs your tests once without chaos, then once per seed with a chaos monkey inside every HttpClient
        (SocketsHttpHandler) and ADO.NET connection and command. Reports tests that only fail under chaos.

          --runs <n>        Seeds 1 to n. Defaults to 5.
          --seed <seed>     One seed, to reproduce a failure.
          --intensity <i>   low, medium or high. Defaults to high.

        Every other option goes to dotnet test, for example --filter or --project.
        """;

    public static int Run(string[] args)
    {
        if (args is not ["test", .. var rest])
        {
            Console.WriteLine(Usage);
            return args is ["--help" or "-h"] ? 0 : 2;
        }

        var seeds = Enumerable.Range(1, 5).ToList();
        var intensity = "high";
        var testArgs = new List<string>();
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--runs" when i + 1 < rest.Length:
                    seeds = Enumerable.Range(1, int.Parse(rest[++i], CultureInfo.InvariantCulture)).ToList();
                    break;
                case "--seed" when i + 1 < rest.Length:
                    seeds = [int.Parse(rest[++i], CultureInfo.InvariantCulture)];
                    break;
                case "--intensity" when i + 1 < rest.Length:
                    intensity = rest[++i];
                    break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    testArgs.Add(rest[i]);
                    break;
            }
        }

        if (!Enum.TryParse<ChaosIntensity>(intensity, ignoreCase: true, out _))
        {
            Console.Error.WriteLine($"dotnet-chaos: unknown intensity '{intensity}'. Use low, medium or high.");
            return 2;
        }

        var root = Directory.CreateTempSubdirectory("dotnet-chaos-").FullName;
        var trx = TrxOptions();

        Console.WriteLine("dotnet-chaos: baseline run, no chaos");
        var baseline = Test(testArgs, Path.Combine(root, "baseline"), trx, chaos: null);
        if (baseline.Passed.Count == 0 && trx.SequenceEqual(MtpTrx))
        {
            // ponytail: xunit.v3 names its TRX option differently; a solution mixing xunit.v3 with other MTP frameworks gets one or the other.
            trx = XunitTrx;
            baseline = Test(testArgs, Path.Combine(root, "baseline-xunit"), trx, chaos: null);
        }

        if (baseline.Passed.Count == 0)
        {
            Console.Error.WriteLine("dotnet-chaos: no test passed without chaos, or no TRX results were written, so there is nothing to compare.");
            return 2;
        }

        Console.WriteLine($"dotnet-chaos: baseline {baseline.Passed.Count} passed, {baseline.Failed.Count} failed");
        var noBuild = testArgs.Contains("--no-build") ? testArgs : [.. testArgs, "--no-build"];
        var broken = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        var reports = new List<string>();
        foreach (var seed in seeds)
        {
            var folder = Path.Combine(root, $"seed-{seed}");
            Console.WriteLine($"dotnet-chaos: seed {seed}");
            var results = Test(noBuild, folder, trx, (seed, intensity));
            if (results.Passed.Count + results.Failed.Count == 0)
            {
                Console.Error.WriteLine($"dotnet-chaos: seed {seed} wrote no test results. The test process may have crashed; see the output above.");
                return 2;
            }

            var failed = results.FailedOnlyUnderChaos(baseline);
            var log = ChaosLog.Read(folder);
            reports.Add($"Seed {seed}: {failed.Count} test(s) failed only under chaos. Faults injected: {log.Totals()}.");
            foreach (var failure in failed)
            {
                reports.Add($"  FAILED {failure.Name}");
                reports.AddRange(log.Explain(failure).Select(line => "    " + line));
                broken.TryAdd(failure.Name, []);
                broken[failure.Name].Add(seed);
            }
        }

        if (broken.Count > 0)
        {
            reports.Add("Tests in parallel share the monkey, so a fault in a test's time window may have hit another test's call.");
        }

        var report = Path.Combine(root, "report.txt");
        File.WriteAllLines(report, reports);
        Console.WriteLine();
        reports.ForEach(Console.WriteLine);
        Console.WriteLine($"Report: {report}");
        Console.WriteLine();
        if (broken.Count == 0)
        {
            Console.WriteLine($"dotnet-chaos: every test that passed without chaos also passed with seeds {string.Join(", ", seeds)}.");
            return 0;
        }

        Console.WriteLine(Monkey);
        Console.WriteLine($"dotnet-chaos: {broken.Count} test(s) pass without chaos but fail under it:");
        foreach (var (test, failedSeeds) in broken)
        {
            Console.WriteLine($"  {test}  (seeds {string.Join(", ", failedSeeds)})");
        }

        Console.WriteLine($"Reproduce: dotnet chaos test --seed {broken.First().Value[0]} --intensity {intensity} {string.Join(' ', testArgs)}".TrimEnd());
        return 1;
    }

    private const string Monkey = """
               .--.  .-"     "-.  .--.
              / .. \/  .-. .-.  \/ .. \
             | |  '|  /   Y   \  |'  | |
             | \   \  \ 0 | 0 /  /   / |
              \ '- ,\.-"`` ``"-./, -' /
               `'-' /_   ^ ^   _\ '-'`
                   |  \._   _./  |
                   \   \ `~` /   /
                    '._ '-=-' _.'
                       '~---~'
          The monkey wins again - mwuhahaha!
        """;

    private static readonly string[] MtpTrx = ["--report-trx"];
    private static readonly string[] XunitTrx = ["--report-xunit-trx"];

    private static TestResults Test(IEnumerable<string> testArgs, string folder, string[] trx, (int Seed, string Intensity)? chaos)
    {
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var arg in (string[])["test", .. testArgs, .. trx, "--results-directory", folder])
        {
            start.ArgumentList.Add(arg);
        }

        if (chaos is { } c)
        {
            var hooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
            var hook = typeof(Runner).Assembly.Location;
            start.Environment["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(hooks) ? hook : $"{hooks}{Path.PathSeparator}{hook}";
            start.Environment[SeedVariable] = c.Seed.ToString(CultureInfo.InvariantCulture);
            start.Environment[IntensityVariable] = c.Intensity;
            start.Environment[OutputVariable] = folder;
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        return TestResults.Read(folder);
    }

    /// <summary>The <c>dotnet test</c> options that write TRX files: Microsoft.Testing.Platform's when global.json selects it, otherwise VSTest's.</summary>
    private static string[] TrxOptions()
    {
        for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "global.json");
            if (File.Exists(file))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var mtp = json.RootElement.TryGetProperty("test", out var test)
                    && test.TryGetProperty("runner", out var runner)
                    && runner.GetString() == "Microsoft.Testing.Platform";
                return mtp ? MtpTrx : ["--logger", "trx"];
            }
        }

        return ["--logger", "trx"];
    }

}

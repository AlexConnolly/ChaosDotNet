using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ChaosDotNet.Reports;

/// <summary>
/// Writes chaos reports and coverage to a folder. <see cref="ChaosMonkey.ExploreAsync"/> and <c>[ChaosTheory]</c> call it for
/// every run when a report folder is set; call it yourself for runs made another way.
/// </summary>
public static class ChaosReports
{
    /// <summary>The environment variable that sets the report folder.</summary>
    public const string DirectoryVariable = "CHAOS_REPORT_DIR";

    private static readonly Dictionary<string, List<ChaosRunRecord>> Runs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>The report folder from <c>CHAOS_REPORT_DIR</c>, or <see langword="null"/> when it is not set.</summary>
    public static string? DirectoryFromEnvironment
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DirectoryVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>
    /// Records one finished run: adds it to the folder's coverage, rewrites the folder's <c>index.html</c>, and writes a report
    /// page for the run when it failed or <paramref name="writeReport"/> is set.
    /// </summary>
    /// <param name="directory">The report folder.</param>
    /// <param name="test">The test's name. Runs with the same name are grouped in coverage.</param>
    /// <param name="scenario">The scenario or monkey after its run. Coverage counts this run.</param>
    /// <param name="failure">The failure's text, when the run failed. Use the exception's <c>ToString()</c> or message.</param>
    /// <param name="shrunk">The smallest failing replay of the run, when it was shrunk. The report's timeline shows this replay.</param>
    /// <param name="writeReport">Write a report page even when the run passed.</param>
    /// <returns>
    /// The report page's full path, or <see langword="null"/> when none was written. A report that cannot be written never fails
    /// the test: the error goes to standard error and the result is <see langword="null"/>.
    /// </returns>
    public static string? Record(string directory, string test, ChaosScenario scenario, string? failure = null, ChaosMonkey? shrunk = null, bool writeReport = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(test);
        ArgumentNullException.ThrowIfNull(scenario);
        try
        {
            return Write(Path.GetFullPath(directory), test, scenario, failure, shrunk, writeReport);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"ChaosDotNet could not write the chaos report to {directory}: {ex.Message}");
            return null;
        }
    }

    private static string? Write(string folder, string test, ChaosScenario scenario, string? failure, ChaosMonkey? shrunk, bool writeReport)
    {
        Directory.CreateDirectory(folder);

        string? report = null;
        if (failure is not null || writeReport)
        {
            report = $"{FileName(test)}-seed-{scenario.Seed}.html";
            var page = shrunk is null
                ? ChaosReport.Create(scenario, test, failure, null)
                : ChaosReport.Create(shrunk, test, failure, (scenario as ChaosMonkey)?.Plan);
            page.WriteHtml(Path.Combine(folder, report));
        }

        var record = ChaosRunRecord.From(scenario, test, failure, report, shrunk?.Plan);
        lock (Gate)
        {
            if (!Runs.TryGetValue(folder, out var runs))
            {
                runs = [];
                Runs[folder] = runs;
            }

            runs.RemoveAll(r => r.Test == record.Test && r.Seed == record.Seed);
            runs.Add(record);
            Save(folder, runs);
            WriteIndex(folder);
        }

        return report is null ? null : Path.Combine(folder, report);
    }

    private static void Save(string folder, List<ChaosRunRecord> runs)
    {
        var data = Path.Combine(folder, ChaosCoverage.DataFolder);
        Directory.CreateDirectory(data);
        var file = Path.Combine(data, $"{FileName(Assembly.GetEntryAssembly()?.GetName().Name ?? "tests")}.json");
        var temp = file + $".{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(runs));
        Retry(() => File.Move(temp, file, overwrite: true));
    }

    private static void WriteIndex(string folder)
    {
        var index = Path.Combine(folder, "index.html");
        var temp = index + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, ChaosCoverage.Load(folder).ToHtml(), Encoding.UTF8);
        Retry(() => File.Move(temp, index, overwrite: true));
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(25);
            }
        }
    }

    internal static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            text.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        return text.Length > 100 ? text.ToString(0, 100) : text.ToString();
    }
}

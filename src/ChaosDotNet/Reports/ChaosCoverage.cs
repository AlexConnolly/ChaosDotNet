using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ChaosDotNet.Reports;

/// <summary>How often one catalogue fault was planned and how often it hit a call.</summary>
/// <param name="Fault">The catalogue entry's name.</param>
/// <param name="Kind">The kind of harm.</param>
/// <param name="Planned">How many runs planned it at least once.</param>
/// <param name="Hit">How many runs had at least one call hit by it.</param>
/// <param name="Calls">How many calls it hit across all runs.</param>
public sealed record ChaosFaultCoverage(string Fault, MonkeyFaultKind Kind, int Planned, int Hit, int Calls);

/// <summary>Coverage of one dependency's fault catalogue.</summary>
/// <param name="Dependency">The dependency's name.</param>
/// <param name="Faults">Each catalogue entry.</param>
public sealed record ChaosDependencyCoverage(string Dependency, IReadOnlyList<ChaosFaultCoverage> Faults)
{
    /// <summary>The share of catalogue entries that hit at least one call, from 0 to 1.</summary>
    public double Ratio => Faults.Count == 0 ? 1 : (double)Faults.Count(f => f.Hit > 0) / Faults.Count;
}

/// <summary>Coverage for the runs of one test.</summary>
/// <param name="Test">The test's name.</param>
/// <param name="Runs">How many runs it had.</param>
/// <param name="Failed">How many of them failed.</param>
/// <param name="Dependencies">Coverage of each dependency it used.</param>
public sealed record ChaosTestCoverage(string Test, int Runs, int Failed, IReadOnlyList<ChaosDependencyCoverage> Dependencies);

/// <summary>A catalogue fault that never hit a call: the code has not been tested against it.</summary>
/// <param name="Dependency">The dependency's name.</param>
/// <param name="Fault">The catalogue entry's name.</param>
/// <param name="Kind">The kind of harm.</param>
/// <param name="Planned">Whether a plan chose it. If so, the fault was active but no call reached the dependency while it was.</param>
public sealed record ChaosCoverageGap(string Dependency, string Fault, MonkeyFaultKind Kind, bool Planned);

/// <summary>One failing run, with its report.</summary>
/// <param name="Test">The test's name.</param>
/// <param name="Seed">The seed.</param>
/// <param name="Failure">The failure.</param>
/// <param name="Report">The report's file name in the report folder, if one was written.</param>
/// <param name="ReproduceWith">The environment variables that replay the smallest failing plan.</param>
public sealed record ChaosRunFailure(string Test, int Seed, string Failure, string? Report, string? ReproduceWith);

/// <summary>
/// Which faults from each dependency's catalogue the chaos runs actually exercised, overall and per test. A fault counts as
/// covered only when it hit a call, not when a plan chose it: a plan can break a dependency the code never calls at that time.
/// </summary>
public sealed class ChaosCoverage
{
    internal const string DataFolder = ".chaos";

    private ChaosCoverage(IReadOnlyList<ChaosRunRecord> runs)
    {
        Runs = runs.Count;
        Dependencies = Aggregate(runs);
        Tests = runs.GroupBy(r => r.Test, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ChaosTestCoverage(g.Key, g.Count(), g.Count(r => r.Failure is not null), Aggregate([.. g])))
            .ToList();
        Failures = runs.Where(r => r.Failure is not null)
            .OrderBy(r => r.Test, StringComparer.Ordinal).ThenBy(r => r.Seed)
            .Select(r => new ChaosRunFailure(r.Test, r.Seed, r.Failure!, r.Report, r.ReproduceWith))
            .ToList();
        Gaps = Dependencies
            .SelectMany(d => d.Faults.Where(f => f.Hit == 0).Select(f => new ChaosCoverageGap(d.Dependency, f.Fault, f.Kind, f.Planned > 0)))
            .ToList();
    }

    /// <summary>How many runs were recorded.</summary>
    public int Runs { get; }

    /// <summary>Coverage of each dependency across every test.</summary>
    public IReadOnlyList<ChaosDependencyCoverage> Dependencies { get; }

    /// <summary>Coverage for each test.</summary>
    public IReadOnlyList<ChaosTestCoverage> Tests { get; }

    /// <summary>Catalogue faults that never hit a call in any test.</summary>
    public IReadOnlyList<ChaosCoverageGap> Gaps { get; }

    /// <summary>Every failing run.</summary>
    public IReadOnlyList<ChaosRunFailure> Failures { get; }

    /// <summary>Loads the coverage recorded in a report folder, from the latest run of each test assembly.</summary>
    public static ChaosCoverage Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var folder = Path.Combine(directory, DataFolder);
        var runs = new List<ChaosRunRecord>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                runs.AddRange(ReadRuns(file));
            }
        }

        return new ChaosCoverage(runs);
    }

    internal static ChaosCoverage From(IReadOnlyList<ChaosRunRecord> runs) => new(runs);

    internal static List<ChaosRunRecord> ReadRuns(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<List<ChaosRunRecord>>(stream) ?? [];
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (JsonException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Renders the summary as a single self-contained HTML page: failures with links to their reports, then coverage.</summary>
    public string ToHtml()
    {
        var html = new StringBuilder();
        HtmlPage.Open(html, "Chaos report");
        html.Append("<header><div class=\"brand\">ChaosDotNet</div><h1>Chaos report</h1><div class=\"meta\">");
        html.Append("<span class=\"badge ").Append(Failures.Count == 0 ? "pass\">All passed" : $"fail\">{Failures.Count} failed").Append("</span>");
        html.Append("<span>").Append(Runs).Append(" run(s)</span><span>").Append(Tests.Count).Append(" test(s)</span>");
        var faults = Dependencies.Sum(d => d.Faults.Count);
        var covered = Dependencies.Sum(d => d.Faults.Count(f => f.Hit > 0));
        html.Append("<span>").Append(covered).Append(" of ").Append(faults).Append(" catalogue faults hit a call</span></div></header>");

        if (Failures.Count > 0)
        {
            html.Append("<section><h2>Failures</h2><table><thead><tr><th>Test</th><th class=\"num\">Seed</th><th>Failure</th><th>Reproduce</th></tr></thead><tbody>");
            foreach (var failure in Failures)
            {
                html.Append("<tr><td>");
                if (failure.Report is not null)
                {
                    html.Append("<a href=\"").Append(E(Uri.EscapeDataString(failure.Report))).Append("\">").Append(E(failure.Test)).Append("</a>");
                }
                else
                {
                    html.Append(E(failure.Test));
                }

                html.Append("</td><td class=\"num\">").Append(failure.Seed.ToString(CultureInfo.InvariantCulture)).Append("</td><td>").Append(E(FirstLine(failure.Failure)));
                html.Append("</td><td><code>").Append(E(failure.ReproduceWith ?? string.Empty)).Append("</code></td></tr>");
            }

            html.Append("</tbody></table></section>");
        }

        html.Append("<section><h2>Coverage</h2>");
        if (Gaps.Count > 0)
        {
            html.Append("<p>These faults never hit a call, so the code has not been tested against them:</p><ul>");
            foreach (var gap in Gaps)
            {
                html.Append("<li><b>").Append(E(gap.Dependency)).Append("</b> ").Append(E(gap.Fault)).Append(" <span class=\"muted\">(").Append(gap.Kind)
                    .Append(gap.Planned ? ", planned but no call reached the dependency while it was active" : ", never planned: add runs").Append(")</span></li>");
            }

            html.Append("</ul>");
        }

        AppendDependencies(html, Dependencies);
        html.Append("</section>");

        if (Tests.Count > 0)
        {
            html.Append("<section><h2>Per test</h2>");
            foreach (var test in Tests)
            {
                html.Append("<details><summary><b>").Append(E(test.Test)).Append("</b> <span class=\"muted\">").Append(test.Runs).Append(" run(s), ")
                    .Append(test.Failed).Append(" failed, ").Append(test.Dependencies.Sum(d => d.Faults.Count(f => f.Hit > 0))).Append(" of ")
                    .Append(test.Dependencies.Sum(d => d.Faults.Count)).Append(" faults hit</span></summary>");
                AppendDependencies(html, test.Dependencies);
                html.Append("</details>");
            }

            html.Append("</section>");
        }

        HtmlPage.Close(html);
        return html.ToString();
    }

    /// <summary>Writes the summary page, creating the folder if needed.</summary>
    public void WriteHtml(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, ToHtml(), Encoding.UTF8);
    }

    private static void AppendDependencies(StringBuilder html, IReadOnlyList<ChaosDependencyCoverage> dependencies)
    {
        if (dependencies.Count == 0)
        {
            html.Append("<p class=\"muted\">No dependencies.</p>");
            return;
        }

        html.Append("<table><thead><tr><th>Dependency</th><th>Fault</th><th>Kind</th><th class=\"num\">Runs planned</th><th class=\"num\">Runs hit</th><th class=\"num\">Calls hit</th></tr></thead><tbody>");
        foreach (var dependency in dependencies)
        {
            var first = true;
            foreach (var fault in dependency.Faults)
            {
                html.Append("<tr><td>");
                if (first)
                {
                    html.Append("<b>").Append(E(dependency.Dependency)).Append("</b> <span class=\"muted\">").Append(dependency.Ratio.ToString("P0", CultureInfo.InvariantCulture)).Append("</span>");
                    first = false;
                }

                html.Append("</td><td>").Append(E(fault.Fault)).Append("</td><td><i class=\"bar k-").Append(fault.Kind.ToString().ToLowerInvariant()).Append("\"></i>").Append(fault.Kind);
                html.Append("</td><td class=\"num\">").Append(fault.Planned).Append("</td><td class=\"num").Append(fault.Hit == 0 ? " gap" : string.Empty).Append("\">").Append(fault.Hit);
                html.Append("</td><td class=\"num\">").Append(fault.Calls).Append("</td></tr>");
            }
        }

        html.Append("</tbody></table>");
    }

    private static List<ChaosDependencyCoverage> Aggregate(IReadOnlyList<ChaosRunRecord> runs) =>
        runs.SelectMany(r => r.Dependencies)
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ChaosDependencyCoverage(
                g.Key,
                g.SelectMany(d => d.Faults)
                    .GroupBy(f => f.Fault, StringComparer.Ordinal)
                    .Select(f => new ChaosFaultCoverage(f.Key, f.First().Kind, f.Count(x => x.Planned > 0), f.Count(x => x.Calls > 0), f.Sum(x => x.Calls)))
                    .OrderBy(f => f.Kind).ThenBy(f => f.Fault, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length > 300 ? line[..300] + "..." : line;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}

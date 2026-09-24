using System.Globalization;
using System.Net;
using System.Text;

namespace ChaosDotNet.Reports;

/// <summary>One call or fault on a dependency's lane, relative to the start of the run.</summary>
/// <param name="At">When it happened, from the start of the run.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Operation">The call's operation.</param>
/// <param name="Details">The call's details, such as the SQL text or HTTP path.</param>
/// <param name="Fault">The injected fault's name.</param>
/// <param name="Error">The real client's error, for failed calls.</param>
public sealed record ChaosReportEvent(TimeSpan At, ChaosEventKind Kind, string? Operation, string? Details, string? Fault, string? Error);

/// <summary>One dependency in a report.</summary>
/// <param name="Dependency">The dependency's name.</param>
/// <param name="Events">Its calls and faults, in time order.</param>
public sealed record ChaosReportLane(string Dependency, IReadOnlyList<ChaosReportEvent> Events);

/// <summary>
/// What happened in one chaos run: the plan, every call and fault per dependency, and the result. <see cref="ToHtml"/>
/// renders it as a single self-contained HTML page.
/// </summary>
public sealed class ChaosReport
{
    private ChaosReport(string title, int seed, string? failure, ChaosPlan plan, ChaosPlan? shrunkPlan, TimeSpan length, IReadOnlyList<ChaosReportLane> lanes)
    {
        Title = title;
        Seed = seed;
        Failure = failure;
        Plan = plan;
        ShrunkPlan = shrunkPlan;
        Length = length;
        Lanes = lanes;
    }

    /// <summary>The report's title, usually the test name.</summary>
    public string Title { get; }

    /// <summary>The run's seed.</summary>
    public int Seed { get; }

    /// <summary>Whether the run passed.</summary>
    public bool Passed => Failure is null;

    /// <summary>The failure, when the run failed.</summary>
    public string? Failure { get; }

    /// <summary>The plan to show: the full plan when the run was shrunk, otherwise the run's own plan. Empty for scenarios that are not monkeys.</summary>
    public ChaosPlan Plan { get; }

    /// <summary>The smallest failing plan, which the timeline's calls come from, when the run was shrunk.</summary>
    public ChaosPlan? ShrunkPlan { get; }

    /// <summary>How long the run lasted on its clock.</summary>
    public TimeSpan Length { get; }

    /// <summary>One lane per dependency.</summary>
    public IReadOnlyList<ChaosReportLane> Lanes { get; }

    /// <summary>Builds a report from a scenario or monkey after its run.</summary>
    /// <param name="scenario">The scenario or monkey whose calls the timeline shows. For a shrunk failure, pass the smallest failing replay.</param>
    /// <param name="title">The title, usually the test name.</param>
    /// <param name="failure">The failure, when the run failed.</param>
    /// <param name="fullPlan">For a shrunk replay, the plan it was shrunk from. Its incidents that the replay left out are shown faded.</param>
    public static ChaosReport From(ChaosScenario scenario, string title, Exception? failure = null, ChaosPlan? fullPlan = null) =>
        Create(scenario, title, failure is null ? null : Describe(failure), fullPlan);

    internal static string Describe(Exception failure) => $"{failure.GetType().Name}: {failure.Message}";

    internal static ChaosReport Create(ChaosScenario scenario, string title, string? failure, ChaosPlan? fullPlan)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(title);
        var own = scenario is ChaosMonkey monkey ? monkey.Plan : new ChaosPlan(scenario.Seed, [], null);
        var shrunkPlan = fullPlan is null ? null : own;
        var plan = fullPlan ?? own;
        var start = scenario.StartedAt ?? scenario.Clock.GetUtcNow();
        var lanes = scenario.Engines.Select(engine => new ChaosReportLane(
            engine.Name,
            engine.Log
                .Where(e => e.Kind is ChaosEventKind.FaultInjected or ChaosEventKind.FaultSkipped or ChaosEventKind.CallSucceeded or ChaosEventKind.CallFailed)
                .Select(e => new ChaosReportEvent(e.Timestamp - start, e.Kind, e.Operation, e.Details, e.Fault, e.Exception is null ? null : Describe(e.Exception)))
                .ToList())).ToList();
        var lastEvent = lanes.SelectMany(l => l.Events).Select(e => e.At).DefaultIfEmpty(TimeSpan.Zero).Max();
        var planEnd = plan.Incidents.Select(i => i.End).DefaultIfEmpty(TimeSpan.Zero).Max();
        var length = new[] { lastEvent, planEnd, TimeSpan.FromSeconds(1) }.Max();
        return new ChaosReport(title, scenario.Seed, failure, plan, shrunkPlan, length, lanes);
    }

    /// <summary>Writes the report as a single HTML file, creating the folder if needed.</summary>
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

    /// <summary>Renders the report as a single self-contained HTML page: inline CSS and SVG, no external files.</summary>
    public string ToHtml()
    {
        var html = new StringBuilder();
        HtmlPage.Open(html, $"{Title} (seed {Seed})");
        html.Append("<header><div class=\"brand\">ChaosDotNet</div><h1>").Append(E(Title)).Append("</h1>");
        html.Append("<div class=\"meta\"><span class=\"badge ").Append(Passed ? "pass\">Passed" : "fail\">Failed").Append("</span>");
        html.Append("<span>Seed ").Append(Seed.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        html.Append("<span>").Append(Plan.Incidents.Count.ToString(CultureInfo.InvariantCulture)).Append(" incident(s)</span>");
        html.Append("<span>").Append(Seconds(Length)).Append(" run</span>");
        var replay = (ShrunkPlan ?? Plan).ReproduceWith;
        html.Append("<button class=\"copy\" onclick=\"navigator.clipboard.writeText(this.dataset.text)\" data-text=\"").Append(E(replay)).Append("\" title=\"Copy\">Reproduce: <code>").Append(E(replay)).Append("</code></button>");
        html.Append("</div></header>");

        if (Failure is not null)
        {
            html.Append("<pre class=\"failure\">").Append(E(Failure)).Append("</pre>");
        }

        html.Append("<section><h2>Timeline</h2>");
        AppendTimeline(html);
        html.Append("<div class=\"legend\"><span><i class=\"dot ok\"></i>call passed through</span><span><i class=\"dot fault\"></i>fault injected</span>");
        html.Append("<span><i class=\"dot skip\"></i>fault skipped (flaky)</span><span><i class=\"dot error\"></i>real client failed</span>");
        foreach (var kind in Enum.GetValues<MonkeyFaultKind>())
        {
            html.Append("<span><i class=\"bar k-").Append(kind.ToString().ToLowerInvariant()).Append("\"></i>").Append(kind).Append("</span>");
        }

        if (ShrunkPlan is not null)
        {
            html.Append("<span><i class=\"bar faded\"></i>not needed to fail</span>");
        }

        html.Append("</div></section>");

        AppendPlan(html);
        AppendEvents(html);
        HtmlPage.Close(html);
        return html.ToString();
    }

    private void AppendTimeline(StringBuilder html)
    {
        const int labelWidth = 170, plotWidth = 900, laneHeight = 46, top = 28;
        var height = top + (Lanes.Count * laneHeight) + 8;
        var scale = plotWidth / Length.TotalSeconds;
        double X(TimeSpan at) => labelWidth + (Math.Clamp(at.TotalSeconds, 0, Length.TotalSeconds) * scale);
        string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        html.Append("<div class=\"scroll\"><svg class=\"timeline\" viewBox=\"0 0 ").Append(labelWidth + plotWidth + 20).Append(' ').Append(height)
            .Append("\" role=\"img\" aria-label=\"Timeline of calls and incidents\">");

        var step = new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.FirstOrDefault(s => Length.TotalSeconds / s <= 12);
        if (step == 0)
        {
            step = (int)Math.Ceiling(Length.TotalSeconds / 12 / 600) * 600;
        }
        for (var t = 0; t <= Length.TotalSeconds + 0.001; t += step)
        {
            var x = X(TimeSpan.FromSeconds(t));
            html.Append("<line class=\"grid\" x1=\"").Append(N(x)).Append("\" x2=\"").Append(N(x)).Append("\" y1=\"").Append(top - 6).Append("\" y2=\"").Append(height - 4).Append("\"/>");
            html.Append("<text class=\"tick\" x=\"").Append(N(x)).Append("\" y=\"14\">").Append(t.ToString(CultureInfo.InvariantCulture)).Append("s</text>");
        }

        var shrunk = ShrunkPlan?.Incidents.Select(i => i.Number).ToHashSet();
        for (var laneIndex = 0; laneIndex < Lanes.Count; laneIndex++)
        {
            var lane = Lanes[laneIndex];
            var y = top + (laneIndex * laneHeight);
            var middle = y + (laneHeight / 2.0);
            html.Append("<text class=\"lane\" x=\"8\" y=\"").Append(N(middle + 4)).Append("\">").Append(E(lane.Dependency)).Append("</text>");
            html.Append("<line class=\"axis\" x1=\"").Append(labelWidth).Append("\" x2=\"").Append(labelWidth + plotWidth).Append("\" y1=\"").Append(N(middle)).Append("\" y2=\"").Append(N(middle)).Append("\"/>");

            foreach (var incident in Plan.Incidents)
            {
                var fault = incident.Faults.FirstOrDefault(f => f.Dependency == lane.Dependency);
                if (fault is null)
                {
                    continue;
                }

                var x1 = X(incident.Start);
                var width = Math.Max(2, X(incident.End) - x1);
                var faded = shrunk is not null && !shrunk.Contains(incident.Number);
                html.Append("<g class=\"incident k-").Append(fault.Kind.ToString().ToLowerInvariant()).Append(faded ? " faded" : string.Empty).Append("\">");
                html.Append("<title>").Append(E(incident.ToString())).Append(faded ? " (not needed to fail)" : string.Empty).Append("</title>");
                html.Append("<rect x=\"").Append(N(x1)).Append("\" y=\"").Append(y + 6).Append("\" width=\"").Append(N(width)).Append("\" height=\"").Append(laneHeight - 12).Append("\" rx=\"4\"/>");
                if (width > 60)
                {
                    html.Append("<text x=\"").Append(N(x1 + 5)).Append("\" y=\"").Append(y + 17).Append("\">#").Append(incident.Number).Append(' ').Append(E(fault.Fault)).Append("</text>");
                }

                html.Append("</g>");
            }

            foreach (var e in lane.Events)
            {
                var css = e.Kind switch
                {
                    ChaosEventKind.FaultInjected => "fault",
                    ChaosEventKind.FaultSkipped => "skip",
                    ChaosEventKind.CallFailed => "error",
                    _ => "ok",
                };
                html.Append("<circle class=\"dot ").Append(css).Append("\" cx=\"").Append(N(X(e.At))).Append("\" cy=\"").Append(N(middle)).Append("\" r=\"").Append(css == "ok" ? "3" : "4.5").Append("\"><title>");
                html.Append(E(Describe(e))).Append("</title></circle>");
            }
        }

        html.Append("</svg></div>");
    }

    private void AppendPlan(StringBuilder html)
    {
        html.Append("<section><h2>Plan</h2>");
        if (Plan.Incidents.Count == 0)
        {
            html.Append("<p class=\"muted\">No incidents.</p></section>");
            return;
        }

        var shrunk = ShrunkPlan?.Incidents.Select(i => i.Number).ToHashSet();
        html.Append("<table><thead><tr><th>#</th><th>Start</th><th>End</th><th>Dependency</th><th>Kind</th><th>Fault</th><th>Share of calls</th>");
        if (shrunk is not null)
        {
            html.Append("<th>Needed to fail</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var incident in Plan.Incidents)
        {
            foreach (var fault in incident.Faults)
            {
                html.Append("<tr><td>").Append(incident.Number).Append("</td><td>").Append(Seconds(incident.Start)).Append("</td><td>").Append(Seconds(incident.End));
                html.Append("</td><td>").Append(E(fault.Dependency)).Append("</td><td><i class=\"bar k-").Append(fault.Kind.ToString().ToLowerInvariant()).Append("\"></i>").Append(fault.Kind);
                html.Append("</td><td>").Append(E(fault.Fault)).Append("</td><td>").Append(incident.Rate < 1 ? incident.Rate.ToString("P0", CultureInfo.InvariantCulture) : "all").Append("</td>");
                if (shrunk is not null)
                {
                    html.Append("<td>").Append(shrunk.Contains(incident.Number) ? "<b>yes</b>" : "no").Append("</td>");
                }

                html.Append("</tr>");
            }
        }

        html.Append("</tbody></table></section>");
    }

    private void AppendEvents(StringBuilder html)
    {
        var events = Lanes.SelectMany(l => l.Events.Select(e => (l.Dependency, Event: e))).OrderBy(x => x.Event.At).ToList();
        html.Append("<section><details><summary><h2>All events (").Append(events.Count).Append(")</h2></summary>");
        html.Append("<table><thead><tr><th>At</th><th>Dependency</th><th>What</th><th>Operation</th><th>Details</th><th>Fault or error</th></tr></thead><tbody>");
        foreach (var (dependency, e) in events.Take(2000))
        {
            html.Append("<tr><td>").Append(Seconds(e.At)).Append("</td><td>").Append(E(dependency)).Append("</td><td>").Append(e.Kind).Append("</td><td>").Append(E(e.Operation ?? string.Empty));
            html.Append("</td><td><code>").Append(E(e.Details ?? string.Empty)).Append("</code></td><td>").Append(E(e.Fault ?? e.Error ?? string.Empty)).Append("</td></tr>");
        }

        html.Append("</tbody></table>");
        if (events.Count > 2000)
        {
            html.Append("<p class=\"muted\">Showing the first 2000 events.</p>");
        }

        html.Append("</details></section>");
    }

    private static string Describe(ChaosReportEvent e)
    {
        var text = new StringBuilder(Seconds(e.At)).Append(' ').Append(e.Operation);
        if (e.Details is not null)
        {
            text.Append(' ').Append(e.Details);
        }

        text.Append(e.Kind switch
        {
            ChaosEventKind.FaultInjected => $" | fault: {e.Fault}",
            ChaosEventKind.FaultSkipped => " | fault skipped (flaky)",
            ChaosEventKind.CallFailed => $" | real client failed: {e.Error}",
            _ => " | passed through",
        });
        return text.ToString();
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string E(string text) => WebUtility.HtmlEncode(text);
}

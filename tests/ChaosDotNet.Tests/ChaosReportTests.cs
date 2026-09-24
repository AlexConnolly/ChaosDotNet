using ChaosDotNet.Reports;

namespace ChaosDotNet.Tests;

public sealed class ChaosReportTests : IDisposable
{
    private static readonly MonkeyFault Down = new("Down", MonkeyFaultKind.Outage, _ => new FailFault(_ => new ChaosTestException()));
    private static readonly MonkeyFault Slow = new("Slow", MonkeyFaultKind.Slowness, _ => new LatencyFault(TimeSpan.FromMilliseconds(10)));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "chaos-report-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Report_shows_the_plan_and_each_call_on_its_dependency_lane()
    {
        var monkey = await RunAsync(seed: 3, name: "orders");

        var report = ChaosReport.From(monkey, "Orders test");
        var html = report.ToHtml();

        Assert.True(report.Passed);
        Assert.Equal(3, report.Seed);
        var lane = Assert.Single(report.Lanes);
        Assert.Equal("orders", lane.Dependency);
        Assert.Contains(lane.Events, e => e.Kind == ChaosEventKind.CallSucceeded && e.Details == "\"sku-1\"");
        Assert.Contains(lane.Events, e => e.Kind == ChaosEventKind.FaultInjected);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("Orders test", html);
        Assert.Contains("CHAOS_SEED=3", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.DoesNotContain("<script src", html);
        foreach (var incident in monkey.Plan.Incidents)
        {
            Assert.Contains($"#{incident.Number} ", html);
        }
    }

    [Fact]
    public async Task Report_escapes_names_and_details()
    {
        var monkey = await RunAsync(seed: 1, name: "<script>alert(1)</script>");

        var html = ChaosReport.From(monkey, "a & b", new InvalidOperationException("<b>boom</b>")).ToHtml();

        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<b>boom</b>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a &amp; b", html);
        Assert.Contains("InvalidOperationException: &lt;b&gt;boom&lt;/b&gt;", html);
    }

    [Fact]
    public async Task Report_of_a_shrunk_failure_marks_incidents_that_were_not_needed()
    {
        var monkey = await RunAsync(seed: 5, name: "orders");
        Assert.True(monkey.Plan.Incidents.Count > 1);
        var needed = monkey.Plan.Incidents[0].Number;
        var shrunk = ChaosMonkey.Replay(5, [needed], new FakeTimeProvider());
        new ProxyFactory<IInventory>(shrunk).Named("orders").WithMonkeyFaults(Down, Slow);
        shrunk.Start();

        var report = ChaosReport.From(shrunk, "Orders", new InvalidOperationException("lost"), monkey.Plan);
        var html = report.ToHtml();

        Assert.False(report.Passed);
        Assert.Equal(monkey.Plan.Incidents.Count, report.Plan.Incidents.Count);
        Assert.Equal(needed, Assert.Single(report.ShrunkPlan!.Incidents).Number);
        Assert.Contains("incident k-", html);
        Assert.Contains(" faded\"", html);
        Assert.Contains("Needed to fail", html);
        Assert.Contains($"CHAOS_INCIDENTS={needed}", html);
    }

    [Fact]
    public async Task Record_writes_a_report_only_for_failures_unless_asked()
    {
        var passed = await RunAsync(seed: 1, name: "orders");
        var failed = await RunAsync(seed: 2, name: "orders");
        var always = await RunAsync(seed: 3, name: "orders");

        Assert.Null(ChaosReports.Record(_folder, "Tests.Orders", passed));
        var report = ChaosReports.Record(_folder, "Tests.Orders", failed, "InvalidOperationException: lost");
        var all = ChaosReports.Record(_folder, "Tests.Orders", always, writeReport: true);

        Assert.Equal(Path.Combine(_folder, "Tests.Orders-seed-2.html"), report);
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(all));
        Assert.False(File.Exists(Path.Combine(_folder, "Tests.Orders-seed-1.html")));
        var index = await File.ReadAllTextAsync(Path.Combine(_folder, "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("1 failed", index);
        Assert.Contains("href=\"Tests.Orders-seed-2.html\"", index);
        Assert.Contains("InvalidOperationException: lost", index);
    }

    [Fact]
    public async Task Coverage_counts_faults_that_hit_calls_and_lists_the_gaps()
    {
        var monkey = await RunAsync(seed: 4, name: "orders", extraDependency: "audit");

        ChaosReports.Record(_folder, "Tests.Orders", monkey);
        var coverage = ChaosCoverage.Load(_folder);

        Assert.Equal(1, coverage.Runs);
        var orders = coverage.Dependencies.Single(d => d.Dependency == "orders");
        Assert.Equal(["Down", "Slow"], orders.Faults.Select(f => f.Fault).Order());
        foreach (var fault in orders.Faults)
        {
            var planned = monkey.Plan.Incidents.Any(i => i.Faults.Any(f => f.Dependency == "orders" && f.Fault == fault.Fault));
            Assert.Equal(planned ? 1 : 0, fault.Planned);
        }

        Assert.Contains(orders.Faults, f => f.Hit == 1 && f.Calls > 0);
        var audit = coverage.Dependencies.Single(d => d.Dependency == "audit");
        Assert.All(audit.Faults, f => Assert.Equal(0, f.Hit));
        Assert.Contains(coverage.Gaps, g => g.Dependency == "audit" && g.Fault == "Down");
        Assert.Equal(0, audit.Ratio);
    }

    [Fact]
    public async Task Coverage_breaks_down_per_test()
    {
        ChaosReports.Record(_folder, "Tests.Orders", await RunAsync(seed: 1, name: "orders"));
        ChaosReports.Record(_folder, "Tests.Orders", await RunAsync(seed: 2, name: "orders"), "boom");
        ChaosReports.Record(_folder, "Tests.Stock", await RunAsync(seed: 1, name: "stock"));

        var coverage = ChaosCoverage.Load(_folder);

        Assert.Equal(3, coverage.Runs);
        Assert.Equal(["Tests.Orders", "Tests.Stock"], coverage.Tests.Select(t => t.Test));
        var orders = coverage.Tests[0];
        Assert.Equal(2, orders.Runs);
        Assert.Equal(1, orders.Failed);
        Assert.Equal("orders", Assert.Single(orders.Dependencies).Dependency);
        Assert.Equal("stock", Assert.Single(coverage.Tests[1].Dependencies).Dependency);
        var failure = Assert.Single(coverage.Failures);
        Assert.Equal(("Tests.Orders", 2), (failure.Test, failure.Seed));
        var html = coverage.ToHtml();
        Assert.Contains("Per test", html);
        Assert.Contains("Tests.Stock", html);
    }

    [Fact]
    public async Task Recording_the_same_seed_again_replaces_the_old_run()
    {
        ChaosReports.Record(_folder, "Tests.Orders", await RunAsync(seed: 1, name: "orders"), "boom");
        ChaosReports.Record(_folder, "Tests.Orders", await RunAsync(seed: 1, name: "orders"));

        var coverage = ChaosCoverage.Load(_folder);

        Assert.Equal(1, coverage.Runs);
        Assert.Empty(coverage.Failures);
    }

    [Fact]
    public async Task Explore_writes_reports_and_names_them_in_the_failure()
    {
        var error = await Assert.ThrowsAsync<ChaosExplorationException>(() => ChaosMonkey.ExploreAsync(3, async monkey =>
        {
            var orders = new ProxyFactory<IInventory>(monkey).Named("orders").WithMonkeyFaults(Down).Create(new Inventory());
            await monkey.RunAsync(async () =>
            {
                for (var i = 0; i < 60; i++)
                {
                    orders.Count("x");
                    await Task.Delay(TimeSpan.FromSeconds(1), monkey.Clock);
                }
            });
        }, new ChaosExploreOptions { ReportDirectory = _folder, Shrink = false }));

        var failure = error.Failures[0];
        Assert.NotNull(failure.ReportPath);
        Assert.True(File.Exists(failure.ReportPath));
        Assert.Contains($"Report: {failure.ReportPath}", error.Message);
        Assert.Contains($"{nameof(ChaosReportTests)}.{nameof(Explore_writes_reports_and_names_them_in_the_failure)}", failure.ReportPath);
        Assert.Equal(3, ChaosCoverage.Load(_folder).Runs);
    }

    [Fact]
    public async Task Explore_writes_nothing_without_a_report_folder()
    {
        Assert.Null(ChaosReports.DirectoryFromEnvironment);

        await ChaosMonkey.ExploreAsync(2, monkey => Task.CompletedTask, new ChaosExploreOptions { Name = "Quiet" });

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task Explore_uses_the_name_option_and_can_report_every_run()
    {
        await ChaosMonkey.ExploreAsync(2, async monkey =>
        {
            new ProxyFactory<IInventory>(monkey).Named("orders").WithMonkeyFaults(Down);
            await monkey.RunAsync(() => Task.CompletedTask);
        }, new ChaosExploreOptions { ReportDirectory = _folder, ReportAll = true, Name = "Checkout flow" });

        Assert.True(File.Exists(Path.Combine(_folder, "Checkout_flow-seed-1.html")));
        Assert.True(File.Exists(Path.Combine(_folder, "Checkout_flow-seed-2.html")));
        Assert.Equal("Checkout flow", Assert.Single(ChaosCoverage.Load(_folder).Tests).Test);
    }

    [Fact]
    public void Report_of_a_run_longer_than_two_hours_renders()
    {
        var monkey = new ChaosMonkey(new FakeTimeProvider(), 1, new ChaosMonkeyOptions { Duration = TimeSpan.FromHours(5), Intensity = ChaosIntensity.Low });
        new ProxyFactory<IInventory>(monkey).Named("orders").WithMonkeyFaults(Down);
        monkey.Start();

        var html = ChaosReport.From(monkey, "Soak").ToHtml();

        Assert.Contains(">3600s<", html);
    }

    [Fact]
    public async Task Record_does_not_throw_when_the_folder_cannot_be_written()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "not-a-folder");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);

        var report = ChaosReports.Record(file, "Tests.Orders", await RunAsync(seed: 1, name: "orders"), "boom");

        Assert.Null(report);
    }

    private static async Task<ChaosMonkey> RunAsync(int seed, string name, string? extraDependency = null)
    {
        var monkey = new ChaosMonkey(new FakeTimeProvider(), seed, new ChaosMonkeyOptions { Duration = TimeSpan.FromSeconds(30), Intensity = ChaosIntensity.High });
        var inventory = new ProxyFactory<IInventory>(monkey).Named(name).WithMonkeyFaults(Down, Slow).Create(new Inventory());
        if (extraDependency is not null)
        {
            new ProxyFactory<IInventory>(monkey).Named(extraDependency).WithMonkeyFaults(Down, Slow);
        }

        await monkey.RunAsync(async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    await inventory.CountAsync("sku-1");
                }
                catch (ChaosTestException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), monkey.Clock);
            }
        });
        return monkey;
    }
}

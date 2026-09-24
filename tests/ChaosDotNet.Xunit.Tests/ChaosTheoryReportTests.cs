using ChaosDotNet.Reports;
using Xunit.Sdk;
using Xunit.v3;

namespace ChaosDotNet.Xunit.Tests;

[TestCaseOrderer(typeof(ByName))]
public sealed class ChaosTheoryReportTests
{
    private const string Folder = "chaos-theory-report-tests";

    [ChaosTheory(Seeds = [1, 2], ReportDirectory = Folder, ReportAll = true)]
    public async Task A_theory_records_each_seed(ChaosMonkey monkey)
    {
        var stock = new ProxyFactory<IStock>(monkey).Named("stock").Create(new Stock());
        await monkey.RunAsync(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    stock.Reserve("a");
                }
                catch (Exception)
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(1), monkey.Clock, TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public void B_the_folder_has_a_report_per_seed_and_coverage()
    {
        const string test = nameof(ChaosTheoryReportTests) + "." + nameof(A_theory_records_each_seed);

        var coverage = ChaosCoverage.Load(Folder);

        var tested = Assert.Single(coverage.Tests, t => t.Test == test);
        Assert.Equal(2, tested.Runs);
        Assert.Equal("stock", Assert.Single(tested.Dependencies).Dependency);
        Assert.True(File.Exists(Path.Combine(Folder, $"{test}-seed-1.html")));
        Assert.True(File.Exists(Path.Combine(Folder, $"{test}-seed-2.html")));
        Assert.True(File.Exists(Path.Combine(Folder, "index.html")));
    }

    private sealed class Stock : IStock
    {
        public int Reserve(string sku) => 1;
    }

    public sealed class ByName : ITestCaseOrderer
    {
        public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
            where TTestCase : notnull, ITestCase =>
            [.. testCases.OrderBy(t => t.TestCaseDisplayName, StringComparer.Ordinal)];
    }
}

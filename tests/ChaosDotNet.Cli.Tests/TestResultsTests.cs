namespace ChaosDotNet.Cli.Tests;

public sealed class TestResultsTests
{
    [Fact]
    public void Reads_outcomes_from_every_trx_file_in_a_folder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(folder, "a.trx"), Trx(("Orders.Places", "Passed"), ("Orders.Refunds", "Failed")));
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "b.trx"), Trx(("Stock.Reserves", "Passed"), ("Stock.Skipped", "NotExecuted")));

        var results = TestResults.Read(folder);

        Assert.Equal(["Orders.Places", "Stock.Reserves"], results.Passed.Order());
        Assert.Equal(["Orders.Refunds"], results.Failed);
    }

    [Fact]
    public void Reports_tests_that_pass_clean_but_fail_under_chaos()
    {
        var baseline = new TestResults(["A", "B", "C"], ["D"]);
        var chaos = new TestResults(["A"], ["B", "D", "E"]);

        Assert.Equal(["B"], chaos.FailedOnlyUnderChaos(baseline));
    }

    private static string Trx(params (string Name, string Outcome)[] tests) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            {string.Concat(tests.Select(t => $"<UnitTestResult testName=\"{t.Name}\" outcome=\"{t.Outcome}\" />"))}
          </Results>
        </TestRun>
        """;
}

namespace ChaosDotNet.Cli.Tests;

public sealed class TestResultsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_outcomes_and_failures_from_every_trx_file_in_a_folder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(folder, "a.trx"), Trx(("Orders.Places", "Passed"), ("Orders.Refunds", "Failed")));
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "b.trx"), Trx(("Stock.Reserves", "Passed"), ("Stock.Skipped", "NotExecuted")));

        var results = TestResults.Read(folder);

        Assert.Equal(["Orders.Places", "Stock.Reserves"], results.Passed.Order());
        var failure = Assert.Single(results.Failed).Value;
        Assert.Equal("Orders.Refunds", failure.Name);
        Assert.Equal("Boom", failure.Message);
        Assert.Contains("at Orders.Refunds()", failure.StackTrace);
        Assert.Equal(At, failure.Start);
        Assert.Equal(At.AddSeconds(2), failure.End);
    }

    [Fact]
    public void Reports_tests_that_pass_clean_but_fail_under_chaos()
    {
        var baseline = new TestResults(["A", "B", "C"], [Failure("D")]);
        var chaos = new TestResults(["A"], [Failure("B"), Failure("D"), Failure("E")]);

        Assert.Equal(["B"], chaos.FailedOnlyUnderChaos(baseline).Select(f => f.Name));
    }

    [Fact]
    public void Reports_tests_that_passed_clean_but_have_no_result_under_chaos()
    {
        var baseline = new TestResults(["A", "B", "C"], [Failure("D")]);
        var chaos = new TestResults(["A"], [Failure("B")]);

        Assert.Equal(["C"], chaos.MissingUnderChaos(baseline));
    }

    private static TestFailure Failure(string name) => new(name, "Boom", "", At, At);

    private static string Trx(params (string Name, string Outcome)[] tests) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            {string.Concat(tests.Select(t => $"""
              <UnitTestResult testName="{t.Name}" outcome="{t.Outcome}" startTime="{At:O}" endTime="{At.AddSeconds(2):O}">
                <Output><ErrorInfo><Message>Boom</Message><StackTrace>   at {t.Name}()</StackTrace></ErrorInfo></Output>
              </UnitTestResult>
              """))}
          </Results>
        </TestRun>
        """;
}

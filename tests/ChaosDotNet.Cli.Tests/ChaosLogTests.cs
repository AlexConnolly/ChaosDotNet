namespace ChaosDotNet.Cli.Tests;

public sealed class ChaosLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly ChaosLog Log = new(
        [
            new LoggedIncident(1, At.AddSeconds(-10), At.AddSeconds(-5), 1.0, [new ChaosIncidentFault("http", "Latency", MonkeyFaultKind.Slowness)]),
            new LoggedIncident(2, At.AddSeconds(1), At.AddSeconds(4), 0.43, [new ChaosIncidentFault("sql", "ConnectionFailure", MonkeyFaultKind.Outage)]),

            // The same incident from a second test process, whose monkey started a little later.
            new LoggedIncident(2, At.AddSeconds(1.2), At.AddSeconds(4.2), 0.43, [new ChaosIncidentFault("sql", "ConnectionFailure", MonkeyFaultKind.Outage)]),
        ],
        [
            new LoggedFault(At.AddSeconds(-6), "http", "Latency", "GET", "/pay"),
            new LoggedFault(At.AddSeconds(1.5), "sql", "Fail", "Open", null),
            new LoggedFault(At.AddSeconds(1.6), "sql", "Fail", "Open", null),
        ]);

    [Fact]
    public void Explains_a_fault_thrown_straight_into_the_test_with_the_infra_state_at_the_time()
    {
        var failure = new TestFailure(
            "Orders.Places",
            "ChaosDotNet.Factories.ChaosDbException : A transport-level error has occurred.\nmore",
            "   at ChaosDotNet.Cli.Interceptors.Open(DbConnection __instance)\n   at Orders.Places()",
            At,
            At.AddSeconds(2));

        var text = string.Join('\n', Log.Explain(failure));

        Assert.Contains("ChaosDbException : A transport-level error has occurred.", text);
        Assert.DoesNotContain("more", text);
        Assert.Contains("injected fault reached the test", text);
        Assert.Single(text.Split('\n'), line => line.Contains("#2 sql ConnectionFailure on 43 % of calls", StringComparison.Ordinal));
        Assert.Contains("sql ConnectionFailure (#2) x2 on Open", text);
        Assert.DoesNotContain("Latency", text);
    }

    [Fact]
    public void Says_when_no_fault_was_active_during_the_test()
    {
        var failure = new TestFailure("Orders.Places", "Assert.Equal() Failure", "   at Orders.Places()", At.AddSeconds(10), At.AddSeconds(11));

        var text = string.Join('\n', Log.Explain(failure));

        Assert.Contains("own assertion", text);
        Assert.Contains("No incident was active", text);
    }
}

using System.Xml.Linq;

namespace ChaosDotNet.Cli;

/// <summary>A failed test, as its TRX file reports it.</summary>
internal sealed record TestFailure(string Name, string Message, string StackTrace, DateTimeOffset Start, DateTimeOffset End);

/// <summary>The tests that passed and failed in one run, read from its TRX files.</summary>
internal sealed record TestResults(IReadOnlySet<string> Passed, IReadOnlyDictionary<string, TestFailure> Failed)
{
    public TestResults(IEnumerable<string> passed, IEnumerable<TestFailure> failed)
        : this(passed.ToHashSet(), failed.DistinctBy(f => f.Name).ToDictionary(f => f.Name))
    {
    }

    public static TestResults Read(string folder)
    {
        var results = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.trx", SearchOption.AllDirectories)
                .SelectMany(file => XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                .ToList()
            : [];
        return new TestResults(
            results.Where(e => (string?)e.Attribute("outcome") == "Passed").Select(e => (string?)e.Attribute("testName") ?? ""),
            results.Where(e => (string?)e.Attribute("outcome") == "Failed").Select(e => new TestFailure(
                (string?)e.Attribute("testName") ?? "",
                Child(e, "Message"),
                Child(e, "StackTrace"),
                (DateTimeOffset?)e.Attribute("startTime") ?? default,
                (DateTimeOffset?)e.Attribute("endTime") ?? default)));

        static string Child(XElement result, string name) =>
            result.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
    }

    /// <summary>Tests that failed here but passed in <paramref name="baseline"/>: the ones chaos broke.</summary>
    public IReadOnlyList<TestFailure> FailedOnlyUnderChaos(TestResults baseline) =>
        Failed.Values.Where(f => baseline.Passed.Contains(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    /// <summary>Tests that passed in <paramref name="baseline"/> but have no result here, usually because chaos crashed their test process.</summary>
    public IReadOnlyList<string> MissingUnderChaos(TestResults baseline) =>
        baseline.Passed.Where(name => !Passed.Contains(name) && !Failed.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
}

using System.Xml.Linq;

namespace ChaosDotNet.Cli;

/// <summary>The names of the tests that passed and failed in one run, read from its TRX files.</summary>
internal sealed record TestResults(IReadOnlySet<string> Passed, IReadOnlySet<string> Failed)
{
    public TestResults(IEnumerable<string> passed, IEnumerable<string> failed)
        : this(passed.ToHashSet(), failed.ToHashSet())
    {
    }

    public static TestResults Read(string folder)
    {
        var results = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.trx", SearchOption.AllDirectories)
                .SelectMany(file => XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                .Select(e => (Name: (string?)e.Attribute("testName") ?? "", Outcome: (string?)e.Attribute("outcome")))
                .ToList()
            : [];
        return new TestResults(
            results.Where(r => r.Outcome == "Passed").Select(r => r.Name),
            results.Where(r => r.Outcome == "Failed").Select(r => r.Name));
    }

    /// <summary>Tests that failed here but passed in <paramref name="baseline"/>: the ones chaos broke.</summary>
    public IReadOnlyList<string> FailedOnlyUnderChaos(TestResults baseline) =>
        Failed.Where(baseline.Passed.Contains).Order(StringComparer.Ordinal).ToList();
}

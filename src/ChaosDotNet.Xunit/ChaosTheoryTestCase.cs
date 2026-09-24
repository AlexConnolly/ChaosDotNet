using System.ComponentModel;
using System.Globalization;
using ChaosDotNet.Reports;
using Microsoft.Extensions.Time.Testing;
using Xunit.Sdk;
using Xunit.v3;

namespace ChaosDotNet.Xunit;

internal sealed record ChaosTheoryOptions(
    int Seed,
    ChaosIntensity Intensity,
    int DurationSeconds,
    bool CorrelatedFaults,
    int MaxConcurrentIncidents,
    bool AllowDataLoss,
    bool RealTime,
    bool Shrink,
    int MaxShrinkRuns,
    string? ReportDirectory,
    bool ReportAll)
{
    public static ChaosTheoryOptions From(ChaosTheoryAttribute attribute, int seed) => new(
        seed,
        attribute.Intensity,
        attribute.DurationSeconds,
        attribute.CorrelatedFaults,
        attribute.MaxConcurrentIncidents,
        attribute.AllowDataLoss,
        attribute.RealTime,
        attribute.Shrink,
        attribute.MaxShrinkRuns,
        attribute.ReportDirectory,
        attribute.ReportAll);

    public ChaosMonkeyOptions Monkey => new()
    {
        Duration = TimeSpan.FromSeconds(DurationSeconds),
        Intensity = Intensity,
        CorrelatedFaults = CorrelatedFaults,
        MaxConcurrentIncidents = MaxConcurrentIncidents,
        AllowDataLoss = AllowDataLoss,
    };
}

/// <summary>One seed of a <see cref="ChaosTheoryAttribute"/> test. Runs itself so it can shrink a failing plan. Not for direct use.</summary>
public sealed class ChaosTheoryTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    private ChaosTheoryOptions _options = null!;

    /// <summary>For deserialization only.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public ChaosTheoryTestCase()
    {
    }

    internal ChaosTheoryTestCase(
        IXunitTestMethod testMethod,
        string displayName,
        string uniqueId,
        bool @explicit,
        Type[]? skipExceptions,
        string? skipReason,
        Type? skipType,
        string? skipUnless,
        string? skipWhen,
        Dictionary<string, HashSet<string>> traits,
        string? sourceFilePath,
        int? sourceLineNumber,
        int? timeout,
        ChaosTheoryOptions options)
        : base(testMethod, displayName, uniqueId, @explicit, skipExceptions, skipReason, skipType, skipUnless, skipWhen, traits, [], sourceFilePath, sourceLineNumber, timeout)
    {
        _options = options;
    }

    /// <summary>The seed this case runs.</summary>
    public int Seed => _options.Seed;

    /// <inheritdoc />
    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager methodFixtureMappings)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        var monkey = CreateMonkey(Incidents());
        var buffer = new BufferingMessageBus();
        var summary = await XunitTestCaseRunner.Instance.Run(
            this,
            [CreateTest(monkey)],
            buffer,
            aggregator,
            cancellationTokenSource,
            parallelMode,
            scheduler,
            TestCaseDisplayName,
            SkipReason,
            explicitOption,
            constructorArguments,
            methodFixtureMappings).ConfigureAwait(false);

        string? note = null;
        ChaosMonkey? smallest = null;
        if (summary.Failed > 0)
        {
            smallest = await ShrinkAsync(monkey, explicitOption, constructorArguments, cancellationTokenSource, parallelMode, scheduler, methodFixtureMappings).ConfigureAwait(false);
            note = Describe(monkey.Plan, smallest.Plan);
        }

        var reports = _options.ReportDirectory ?? ChaosReports.DirectoryFromEnvironment;
        if (reports is not null && summary.Skipped == 0 && summary.NotRun == 0)
        {
            var failure = summary.Failed > 0 ? buffer.FailureText ?? "The test failed." : null;
            var report = ChaosReports.Record(reports, TestName, monkey, failure, smallest == monkey ? null : smallest, _options.ReportAll);
            if (note is not null && report is not null)
            {
                note += $"{Environment.NewLine}Report: {report}";
            }
        }

        buffer.Flush(messageBus, note);
        return summary;
    }

    private string TestName => $"{TestMethod.TestClass.Class.Name}.{TestMethod.MethodName}";

    /// <inheritdoc />
    protected override void Serialize(IXunitSerializationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        base.Serialize(info);
        info.AddValue("ChaosSeed", _options.Seed);
        info.AddValue("ChaosIntensity", (int)_options.Intensity);
        info.AddValue("ChaosDuration", _options.DurationSeconds);
        info.AddValue("ChaosCorrelated", _options.CorrelatedFaults);
        info.AddValue("ChaosConcurrent", _options.MaxConcurrentIncidents);
        info.AddValue("ChaosDataLoss", _options.AllowDataLoss);
        info.AddValue("ChaosRealTime", _options.RealTime);
        info.AddValue("ChaosShrink", _options.Shrink);
        info.AddValue("ChaosShrinkRuns", _options.MaxShrinkRuns);
        info.AddValue("ChaosReportDirectory", _options.ReportDirectory);
        info.AddValue("ChaosReportAll", _options.ReportAll);
    }

    /// <inheritdoc />
    protected override void Deserialize(IXunitSerializationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        base.Deserialize(info);
        _options = new ChaosTheoryOptions(
            info.GetValue<int>("ChaosSeed"),
            (ChaosIntensity)info.GetValue<int>("ChaosIntensity"),
            info.GetValue<int>("ChaosDuration"),
            info.GetValue<bool>("ChaosCorrelated"),
            info.GetValue<int>("ChaosConcurrent"),
            info.GetValue<bool>("ChaosDataLoss"),
            info.GetValue<bool>("ChaosRealTime"),
            info.GetValue<bool>("ChaosShrink"),
            info.GetValue<int>("ChaosShrinkRuns"),
            info.GetValue<string?>("ChaosReportDirectory"),
            info.GetValue<bool>("ChaosReportAll"));
    }

    private static HashSet<int>? Incidents()
    {
        var value = Environment.GetEnvironmentVariable("CHAOS_INCIDENTS");
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToHashSet();
    }

    private TimeProvider Clock() => _options.RealTime ? TimeProvider.System : new FakeTimeProvider();

    private ChaosMonkey CreateMonkey(IEnumerable<int>? only) =>
        only is null
            ? new ChaosMonkey(Clock(), _options.Seed, _options.Monkey)
            : ChaosMonkey.Replay(_options.Seed, only, Clock(), _options.Monkey);

    private XunitTest CreateTest(ChaosMonkey monkey) => new(
        this,
        TestMethod,
        Explicit,
        SkipReason,
        SkipType,
        SkipUnless,
        SkipWhen,
        TestCaseDisplayName,
        0,
        Traits.ToDictionary(t => t.Key, t => (IReadOnlyCollection<string>)t.Value),
        Timeout,
        [monkey],
        TestLabel,
        DisableParallelization);

    private async Task<ChaosMonkey> ShrinkAsync(
        ChaosMonkey original,
        ExplicitOption explicitOption,
        object?[] constructorArguments,
        CancellationTokenSource cancellation,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager fixtureMappings)
    {
        var plan = original.Plan;
        var smallest = original;
        if (_options.Shrink && plan.Incidents.Count > 0)
        {
            var kept = plan.Incidents.Select(i => i.Number).ToHashSet();
            var budget = _options.MaxShrinkRuns;
            foreach (var number in plan.Incidents.Select(i => i.Number).ToList())
            {
                if (budget-- <= 0)
                {
                    break;
                }

                var trial = kept.Where(n => n != number).ToHashSet();
                var monkey = CreateMonkey(trial);
                var result = await XunitTestRunner.Instance.Run(
                    CreateTest(monkey),
                    new SilentMessageBus(),
                    constructorArguments,
                    explicitOption,
                    new ExceptionAggregator(),
                    cancellation,
                    parallelMode,
                    scheduler,
                    TestMethod.BeforeAfterTestAttributes,
                    fixtureMappings).ConfigureAwait(false);
                if (result.Failed > 0)
                {
                    kept = trial;
                    smallest = monkey;
                }
            }
        }

        return smallest;
    }

    private string Describe(ChaosPlan plan, ChaosPlan smallest)
    {
        var text = new System.Text.StringBuilder()
            .AppendLine()
            .AppendLine()
            .Append(CultureInfo.InvariantCulture, $"Chaos seed {Seed}. Smallest failing plan ({smallest.Incidents.Count} of {plan.Incidents.Count} incident(s)):");
        if (smallest.Incidents.Count == 0)
        {
            text.AppendLine().Append("  (no incidents: the test fails without chaos)");
        }

        foreach (var incident in smallest.Incidents)
        {
            text.AppendLine().Append("  ").Append(incident);
        }

        return text.AppendLine().Append("Reproduce: set ").Append(smallest.ReproduceWith).ToString();
    }
}

/// <summary>Holds a test's messages until it finishes, so the failure message can include the shrunk plan.</summary>
internal sealed class BufferingMessageBus : IMessageBus
{
    private readonly List<IMessageSinkMessage> _messages = [];

    public bool QueueMessage(IMessageSinkMessage message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }

        return true;
    }

    /// <summary>The first failure's exception type and message, once the test has failed.</summary>
    public string? FailureText
    {
        get
        {
            lock (_messages)
            {
                var failed = _messages.OfType<ITestFailed>().FirstOrDefault();
                return failed is null || failed.Messages.Length == 0
                    ? null
                    : $"{failed.ExceptionTypes.FirstOrDefault()?.Split('.').Last()}: {failed.Messages[0]}";
            }
        }
    }

    public void Flush(IMessageBus target, string? failureNote)
    {
        List<IMessageSinkMessage> messages;
        lock (_messages)
        {
            messages = [.. _messages];
            _messages.Clear();
        }

        foreach (var message in messages)
        {
            target.QueueMessage(failureNote is not null && message is ITestFailed failed ? WithNote(failed, failureNote) : message);
        }
    }

    public void Dispose()
    {
    }

    private static TestFailed WithNote(ITestFailed failed, string note)
    {
        var messages = failed.Messages.ToArray();
        if (messages.Length > 0)
        {
            messages[0] += note;
        }

        return new TestFailed
        {
            AssemblyUniqueID = failed.AssemblyUniqueID,
            TestCollectionUniqueID = failed.TestCollectionUniqueID,
            TestClassUniqueID = failed.TestClassUniqueID,
            TestMethodUniqueID = failed.TestMethodUniqueID,
            TestCaseUniqueID = failed.TestCaseUniqueID,
            TestUniqueID = failed.TestUniqueID,
            Cause = failed.Cause,
            ExceptionParentIndices = failed.ExceptionParentIndices,
            ExceptionTypes = failed.ExceptionTypes,
            Messages = messages,
            StackTraces = failed.StackTraces,
            ExecutionTime = failed.ExecutionTime,
            FinishTime = failed.FinishTime,
            Output = failed.Output,
            Warnings = failed.Warnings,
        };
    }
}

/// <summary>Drops every message, for the silent runs that shrink a plan.</summary>
internal sealed class SilentMessageBus : IMessageBus
{
    public bool QueueMessage(IMessageSinkMessage message) => true;

    public void Dispose()
    {
    }
}

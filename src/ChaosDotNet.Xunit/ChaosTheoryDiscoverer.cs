using System.Globalization;
using Xunit.Sdk;
using Xunit.v3;

namespace ChaosDotNet.Xunit;

/// <summary>Makes one <see cref="ChaosTheoryTestCase"/> per seed for a <see cref="ChaosTheoryAttribute"/> method. Not for direct use.</summary>
public sealed class ChaosTheoryDiscoverer : IXunitTestCaseDiscoverer
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        ArgumentNullException.ThrowIfNull(testMethod);
        var attribute = (ChaosTheoryAttribute)factAttribute;
        var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, factAttribute);
        var baseName = attribute.DisplayName ?? $"{testMethod.Method.ReflectedType?.FullName}.{testMethod.Method.Name}";

        var parameters = testMethod.Method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ChaosMonkey))
        {
            IXunitTestCase error = new ExecutionErrorTestCase(
                testMethod,
                details.TestCaseDisplayName,
                details.UniqueID,
                details.SourceFilePath,
                details.SourceLineNumber,
                $"[ChaosTheory] methods take exactly one ChaosMonkey parameter; {testMethod.Method.Name} has {parameters.Length} parameter(s).");
            return new([error]);
        }

        var cases = Seeds(attribute).Select(seed => (IXunitTestCase)new ChaosTheoryTestCase(
            testMethod,
            $"{baseName}(seed: {seed.ToString(CultureInfo.InvariantCulture)})",
            $"{details.UniqueID}:chaos-seed:{seed.ToString(CultureInfo.InvariantCulture)}",
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToDictionary(t => t.Key, t => t.Value.ToHashSet()),
            details.SourceFilePath,
            details.SourceLineNumber,
            details.Timeout,
            ChaosTheoryOptions.From(attribute, seed))).ToArray();

        return new(cases);
    }

    private static IEnumerable<int> Seeds(ChaosTheoryAttribute attribute)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
        {
            return [single];
        }

        if (attribute.Seeds is { Length: > 0 } seeds)
        {
            return seeds;
        }

        var runs = Math.Max(1, attribute.Runs);
        return string.Equals(Environment.GetEnvironmentVariable("CHAOS_EXPLORE"), "random", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Range(0, runs).Select(_ => Random.Shared.Next()).ToArray()
            : Enumerable.Range(1, runs);
    }
}

using System.Runtime.CompilerServices;
using ChaosDotNet.Xunit;
using Xunit;
using Xunit.v3;

namespace ChaosDotNet;

/// <summary>
/// Runs a test once per seed, each seed as its own test case named like <c>Orders(seed: 3)</c>. The test takes one
/// <see cref="ChaosMonkey"/> parameter, built for the case's seed with a fake clock. A failing case shrinks its plan to the
/// fewest incidents that still fail and names them in the failure message.
/// </summary>
/// <example>
/// <code>
/// [ChaosTheory(Runs = 50)]
/// public async Task Orders_are_never_lost(ChaosMonkey monkey)
/// {
///     // ... build the app with factories joined to the monkey ...
///     await monkey.RunAsync(() => PlaceOrders(app));
///     Assert.Equal(charges, orders);
/// }
/// </code>
/// </example>
[XunitTestCaseDiscoverer(typeof(ChaosTheoryDiscoverer))]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ChaosTheoryAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : FactAttribute(sourceFilePath, sourceLineNumber)
{
    /// <summary>How many seeds to run, 1 to <see cref="Runs"/>. Defaults to 20. <c>CHAOS_EXPLORE=random</c> picks new seeds instead.</summary>
    public int Runs { get; set; } = 20;

    /// <summary>Exact seeds to run instead of 1 to <see cref="Runs"/>, for example seeds that failed before.</summary>
    public int[]? Seeds { get; set; }

    /// <summary>How much chaos to cause. Defaults to <see cref="ChaosIntensity.Medium"/>.</summary>
    public ChaosIntensity Intensity { get; set; } = ChaosIntensity.Medium;

    /// <summary>How long each plan lasts, in seconds. Defaults to 60.</summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>Whether some incidents hit several dependencies at once. Defaults to <see langword="true"/>.</summary>
    public bool CorrelatedFaults { get; set; } = true;

    /// <summary>The most incidents active at once. Defaults to 2.</summary>
    public int MaxConcurrentIncidents { get; set; } = 2;

    /// <summary>Whether data-loss faults such as dropped messages may be used. Defaults to <see langword="false"/>.</summary>
    public bool AllowDataLoss { get; set; }

    /// <summary>Use the system clock instead of a fake one, for tests against real containers.</summary>
    public bool RealTime { get; set; }

    /// <summary>
    /// Whether a failing case shrinks its plan. Defaults to <see langword="true"/>. Shrinking re-runs the whole test, silently:
    /// the test class constructor, before/after attributes and the body, up to <see cref="MaxShrinkRuns"/> times. Turn it off
    /// if the test has side effects that must not repeat.
    /// </summary>
    public bool Shrink { get; set; } = true;

    /// <summary>The most extra runs spent shrinking one failing case. Defaults to 30.</summary>
    public int MaxShrinkRuns { get; set; } = 30;
}

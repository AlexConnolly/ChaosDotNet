using System.Text;

namespace ChaosDotNet;

/// <summary>Checks what happened on a factory's timeline. Each check throws <see cref="ChaosAssertionException"/> when it fails.</summary>
public sealed class ChaosVerifier
{
    private readonly ChaosEngine _engine;

    internal ChaosVerifier(ChaosEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Checks how many faults were injected, in all windows or in one window.</summary>
    public ChaosVerifier FaultsInjected(int? atLeast = null, int? atMost = null, int? exactly = null, int? window = null)
    {
        var log = _engine.Log;
        var count = log.Count(e => e.Kind == ChaosEventKind.FaultInjected && (window is null || e.Window == window));
        var scope = window is null ? "in all windows" : $"in window {window}";
        CheckCount(log, count, $"faults injected {scope}", atLeast, atMost, exactly);
        return this;
    }

    /// <summary>Checks how many calls reached the real client and succeeded.</summary>
    public ChaosVerifier CallsSucceeded(int? atLeast = null, int? atMost = null, int? exactly = null)
    {
        var log = _engine.Log;
        var count = log.Count(e => e.Kind == ChaosEventKind.CallSucceeded);
        CheckCount(log, count, "calls succeeded", atLeast, atMost, exactly);
        return this;
    }

    /// <summary>
    /// Checks that, after the window of the last injected fault ended, a call succeeded within the given time.
    /// </summary>
    public ChaosVerifier RecoveredWithin(double amount, TimeUnit unit)
    {
        var limit = unit.ToTimeSpan(amount);
        var log = _engine.Log;

        var lastFault = log.LastOrDefault(e => e.Kind == ChaosEventKind.FaultInjected)
            ?? throw Fail(log, "No fault was injected, so there is nothing to recover from.");

        var windowEnd = log.LastOrDefault(e => e.Kind == ChaosEventKind.WindowEnded && e.Window == lastFault.Window)?.Timestamp
            ?? lastFault.WindowEndsAt;

        if (windowEnd is null || windowEnd > _engine.Clock.GetUtcNow())
        {
            throw Fail(log, $"Window {lastFault.Window} has not ended yet.");
        }

        var recovered = log.FirstOrDefault(e => e.Kind == ChaosEventKind.CallSucceeded && e.Timestamp >= windowEnd)
            ?? throw Fail(log, $"No call succeeded after window {lastFault.Window} ended at {windowEnd:HH:mm:ss.fff}.");

        var took = recovered.Timestamp - windowEnd.Value;
        if (took > limit)
        {
            throw Fail(log, $"Recovery took {took}, which is more than {limit}.");
        }

        return this;
    }

    /// <summary>Checks that no call is frozen right now.</summary>
    public ChaosVerifier NoCallsFrozen()
    {
        var frozen = _engine.FrozenCalls;
        if (frozen > 0)
        {
            throw Fail(_engine.Log, $"{frozen} call(s) are still frozen.");
        }

        return this;
    }

    private static void CheckCount(IReadOnlyList<ChaosEvent> log, int count, string what, int? atLeast, int? atMost, int? exactly)
    {
        if (atLeast is null && atMost is null && exactly is null)
        {
            atLeast = 1;
        }

        if (exactly is not null && count != exactly)
        {
            throw Fail(log, $"Expected exactly {exactly} {what}, but there were {count}.");
        }

        if (atLeast is not null && count < atLeast)
        {
            throw Fail(log, $"Expected at least {atLeast} {what}, but there were {count}.");
        }

        if (atMost is not null && count > atMost)
        {
            throw Fail(log, $"Expected at most {atMost} {what}, but there were {count}.");
        }
    }

    private static ChaosAssertionException Fail(IReadOnlyList<ChaosEvent> log, string message)
    {
        var text = new StringBuilder(message).AppendLine().AppendLine("Timeline:");
        foreach (var e in log)
        {
            text.Append("  ").AppendLine(e.ToString());
        }

        return new ChaosAssertionException(text.ToString());
    }
}

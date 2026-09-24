namespace ChaosDotNet;

/// <summary>The kind of a <see cref="ChaosEvent"/>.</summary>
public enum ChaosEventKind
{
    /// <summary>The factory's timeline started.</summary>
    TimelineStarted,

    /// <summary>A window became active.</summary>
    WindowStarted,

    /// <summary>A window stopped being active.</summary>
    WindowEnded,

    /// <summary>A fault was applied to a call.</summary>
    FaultInjected,

    /// <summary>A call inside an active window was let through because of <c>Flaky(rate)</c>.</summary>
    FaultSkipped,

    /// <summary>A call reached the real client and succeeded.</summary>
    CallSucceeded,

    /// <summary>A call reached the real client and the real client threw.</summary>
    CallFailed,
}

/// <summary>One entry in a factory's timeline log.</summary>
/// <param name="Timestamp">When the event happened, on the factory's clock.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Window">The zero-based index of the window, when the event belongs to one.</param>
/// <param name="Operation">The call's operation, when the event belongs to a call.</param>
/// <param name="Fault">The fault's name, for <see cref="ChaosEventKind.FaultInjected"/>.</param>
/// <param name="Exception">The real client's exception, for <see cref="ChaosEventKind.CallFailed"/>.</param>
public sealed record ChaosEvent(
    DateTimeOffset Timestamp,
    ChaosEventKind Kind,
    int? Window = null,
    string? Operation = null,
    string? Fault = null,
    Exception? Exception = null)
{
    internal DateTimeOffset? WindowEndsAt { get; init; }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = $"{Timestamp:HH:mm:ss.fff} {Kind}";
        if (Window is not null)
        {
            text += $" window={Window}";
        }

        if (Operation is not null)
        {
            text += $" op={Operation}";
        }

        if (Fault is not null)
        {
            text += $" fault={Fault}";
        }

        if (Exception is not null)
        {
            text += $" error={Exception.GetType().Name}";
        }

        return text;
    }
}

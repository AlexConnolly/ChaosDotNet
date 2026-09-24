namespace ChaosDotNet;

/// <summary>
/// What happens to a call inside an active window. The engine applies <see cref="FreezeFault"/>,
/// <see cref="LatencyFault"/>, <see cref="JitterFault"/> and <see cref="FailFault"/> itself.
/// Any other subtype is handed back to the veneer, which knows how to apply it (for example an HTTP response).
/// </summary>
public abstract class Fault
{
    /// <summary>A short name for the fault, used in the log.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether the fault can apply to a call. Calls it cannot apply to pass through and do not count towards the window.
    /// For example, a cache miss applies only to reads.
    /// </summary>
    public virtual bool AppliesTo(ChaosCall call) => true;

    /// <summary>
    /// Called by the engine each time the fault's window becomes active, including each repeat of an <c>Every(...)</c> window.
    /// Faults that keep state per activation, such as a clock drift anchor, reset it here.
    /// </summary>
    public virtual void Activated()
    {
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The call blocks until the window ends, then continues to the real client.</summary>
public sealed class FreezeFault : Fault
{
    internal static readonly FreezeFault Instance = new();

    private FreezeFault()
    {
    }

    /// <inheritdoc />
    public override string Name => "Freeze";
}

/// <summary>The call waits for a fixed delay, then continues to the real client.</summary>
public sealed class LatencyFault : Fault
{
    /// <summary>Creates a latency fault.</summary>
    public LatencyFault(TimeSpan delay)
    {
        Delay = delay;
    }

    /// <summary>How long each call waits.</summary>
    public TimeSpan Delay { get; }

    /// <inheritdoc />
    public override string Name => $"Latency({Delay})";
}

/// <summary>The call waits for a random delay between two bounds, then continues to the real client.</summary>
public sealed class JitterFault : Fault
{
    /// <summary>Creates a jitter fault.</summary>
    public JitterFault(TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            throw new ArgumentException("The maximum delay must not be less than the minimum delay.", nameof(max));
        }

        Min = min;
        Max = max;
    }

    /// <summary>The shortest delay.</summary>
    public TimeSpan Min { get; }

    /// <summary>The longest delay.</summary>
    public TimeSpan Max { get; }

    /// <inheritdoc />
    public override string Name => $"Jitter({Min}..{Max})";
}

/// <summary>The call throws an exception and does not reach the real client.</summary>
public sealed class FailFault : Fault
{
    private readonly Func<ChaosCall, Exception> _exception;

    /// <summary>Creates a fail fault. The factory runs once per call, so each call gets a new exception.</summary>
    public FailFault(Func<ChaosCall, Exception> exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <inheritdoc />
    public override string Name => "Fail";

    /// <summary>Creates the exception for a call.</summary>
    public Exception CreateException(ChaosCall call) =>
        _exception(call) ?? throw new InvalidOperationException("The exception factory returned null.");
}

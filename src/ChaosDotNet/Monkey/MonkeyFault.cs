namespace ChaosDotNet;

/// <summary>The kind of harm a <see cref="MonkeyFault"/> does.</summary>
public enum MonkeyFaultKind
{
    /// <summary>The dependency is unreachable or hangs.</summary>
    Outage,

    /// <summary>The dependency answers, but slowly.</summary>
    Slowness,

    /// <summary>The dependency answers with an error.</summary>
    Error,

    /// <summary>The dependency answers with something unexpected: malformed data, odd status codes, a response that breaks halfway.</summary>
    Weird,

    /// <summary>The dependency silently loses or hides data. Only used when <see cref="ChaosMonkeyOptions.AllowDataLoss"/> is on.</summary>
    DataLoss,
}

/// <summary>
/// A fault the <see cref="ChaosMonkey"/> can choose for a dependency. Each factory has a catalogue of them;
/// change it with <c>WithMonkeyFaults</c> or <c>AddMonkeyFaults</c>.
/// </summary>
public sealed class MonkeyFault
{
    private readonly Func<Random, Fault> _create;

    /// <summary>Creates a catalogue entry.</summary>
    /// <param name="name">A short name, shown in plans.</param>
    /// <param name="kind">The kind of harm.</param>
    /// <param name="create">Builds the fault. The <see cref="Random"/> comes from the plan's seed, so use it for any randomness in the fault.</param>
    /// <param name="weight">How often the monkey picks this entry compared with the others. Defaults to 1.</param>
    public MonkeyFault(string name, MonkeyFaultKind kind, Func<Random, Fault> create, double weight = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        Name = name;
        Kind = kind;
        Weight = weight;
        _create = create;
    }

    /// <summary>A short name, shown in plans.</summary>
    public string Name { get; }

    /// <summary>The kind of harm.</summary>
    public MonkeyFaultKind Kind { get; }

    /// <summary>How often the monkey picks this entry compared with the others.</summary>
    public double Weight { get; }

    /// <summary>Builds the fault.</summary>
    public Fault Create(Random random) => _create(random) ?? throw new InvalidOperationException($"The monkey fault '{Name}' built no fault.");

    /// <summary>Calls hang until the incident ends.</summary>
    public static MonkeyFault Freeze { get; } = new("Freeze", MonkeyFaultKind.Outage, _ => FreezeFault.Instance);

    /// <summary>Each call waits 0.5 to 5 seconds.</summary>
    public static MonkeyFault Latency { get; } = new("Latency", MonkeyFaultKind.Slowness, random => new LatencyFault(TimeSpan.FromMilliseconds(500 + random.Next(4500))));

    /// <summary>Each call waits a random time up to 3 seconds.</summary>
    public static MonkeyFault Jitter { get; } = new("Jitter", MonkeyFaultKind.Slowness, _ => new JitterFault(TimeSpan.Zero, TimeSpan.FromSeconds(3)));

    /// <summary>Builds an entry that throws a random exception from <paramref name="exceptions"/> on each call.</summary>
    public static MonkeyFault RandomException(string name, MonkeyFaultKind kind, params Func<Exception>[] exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentOutOfRangeException.ThrowIfZero(exceptions.Length);
        return new MonkeyFault(name, kind, random => RandomFail(random, exceptions));
    }

    internal static FailFault RandomFail(Random random, IReadOnlyList<Func<Exception>> exceptions)
    {
        var picker = new Random(random.Next());
        return new FailFault(_ =>
        {
            int index;
            lock (picker)
            {
                index = picker.Next(exceptions.Count);
            }

            return exceptions[index]();
        });
    }
}

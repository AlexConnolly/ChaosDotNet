using ChaosDotNet.Proxies;

namespace ChaosDotNet.Factories;

/// <summary>
/// Builds veneers over any interface, for example your own <c>IPaymentGateway</c>.
/// Every method call goes through the timeline. Async methods honour a <see cref="CancellationToken"/> argument.
/// </summary>
/// <typeparam name="T">The interface to wrap.</typeparam>
public sealed class ProxyFactory<T> : ChaosFactory<ProxyFactory<T>, ProxyChaosCall>
    where T : class
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public ProxyFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public ProxyFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates a veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public T Create(T inner)
    {
        var veneer = ChaosProxy.Create(inner, Engine);
        Engine.Start();
        return veneer;
    }

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        .. base.DefaultMonkeyFaults(),
        MonkeyFault.RandomException(
            "RandomException",
            MonkeyFaultKind.Error,
            () => new TimeoutException("The operation has timed out. (ChaosDotNet)"),
            () => new IOException("Unable to read data from the transport connection. (ChaosDotNet)"),
            () => new InvalidOperationException("The dependency returned an unexpected response. (ChaosDotNet)"),
            () => new ObjectDisposedException("connection", "The connection was disposed. (ChaosDotNet)")),
    ];
}

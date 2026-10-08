using System.Net.Sockets;

namespace ChaosDotNet.Cli;

/// <summary>A TCP socket operation: <c>Connect</c>, <c>Send</c> or <c>Receive</c>.</summary>
internal sealed class SocketChaosCall(string operation, string? endpoint) : ChaosCall(operation)
{
    public override string? Details => endpoint;
}

/// <summary>
/// The timeline for every TCP socket in the process, so clients with their own wire protocol (Redis, Service Bus, RabbitMQ,
/// Mongo, gRPC) break too. Only failures: a socket call cannot wait without blocking a thread.
/// </summary>
internal sealed class SocketFactory : ChaosFactory<SocketFactory, SocketChaosCall>
{
    public SocketFactory()
    {
    }

    public SocketFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        new("ConnectionDropped", MonkeyFaultKind.Outage, _ => new FailFault(call => new SocketException((int)(call.Operation == "Connect" ? SocketError.ConnectionRefused : SocketError.ConnectionReset))), 2),
        new("NetworkTimeout", MonkeyFaultKind.Error, _ => new FailFault(_ => new SocketException((int)SocketError.TimedOut))),
    ];
}

using Azure.Messaging.ServiceBus;
using ChaosDotNet.AzureServiceBus;

namespace ChaosDotNet.Factories;

/// <summary>A Service Bus call made through a <see cref="ServiceBusFactory"/> veneer.</summary>
public sealed class ServiceBusChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    /// <param name="operation">
    /// One of <c>Send</c>, <c>Schedule</c>, <c>CancelScheduled</c>, <c>Receive</c>, <c>ReceiveDeferred</c>, <c>Peek</c>,
    /// <c>Complete</c>, <c>Abandon</c>, <c>DeadLetter</c>, <c>Defer</c>, <c>RenewLock</c> or <c>Process</c>.
    /// </param>
    /// <param name="entityPath">The queue, topic or subscription path.</param>
    /// <param name="messages">The messages being sent or scheduled.</param>
    /// <param name="receivedMessage">The received message being settled or processed.</param>
    public ServiceBusChaosCall(
        string operation,
        string entityPath,
        IReadOnlyList<ServiceBusMessage>? messages = null,
        ServiceBusReceivedMessage? receivedMessage = null)
        : base(operation)
    {
        EntityPath = entityPath;
        Messages = messages ?? [];
        ReceivedMessage = receivedMessage;
    }

    /// <summary>The queue, topic or subscription path.</summary>
    public string EntityPath { get; }

    /// <summary>The messages being sent or scheduled. Empty for other operations.</summary>
    public IReadOnlyList<ServiceBusMessage> Messages { get; }

    /// <summary>The received message being settled or processed. <see langword="null"/> for other operations.</summary>
    public ServiceBusReceivedMessage? ReceivedMessage { get; }

    /// <inheritdoc />
    public override string? Details
    {
        get
        {
            var subject = ReceivedMessage?.Subject ?? Messages.FirstOrDefault()?.Subject;
            return subject is null ? EntityPath : $"{EntityPath}: {subject}";
        }
    }
}

/// <summary>
/// Builds <see cref="ServiceBusClient"/> veneers. Senders, receivers and processors created from the veneer follow the timeline.
/// Session receivers, session processors and rule managers pass through.
/// </summary>
/// <example>
/// <code>
/// var bus = new ServiceBusFactory()
///     .When(call => call.Operation == "Send").For(30, TimeUnit.Seconds).ServiceBusy()
///     .Then().When(call => call.Operation == "Process").ForCalls(1).Fail&lt;InvalidOperationException&gt;();
///
/// ServiceBusClient client = bus.CreateClient(new ServiceBusClient(connectionString));
/// </code>
/// </example>
public sealed class ServiceBusFactory : ChaosFactory<ServiceBusFactory, ServiceBusChaosCall>
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public ServiceBusFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public ServiceBusFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates a <see cref="ServiceBusClient"/> veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public ServiceBusClient CreateClient(ServiceBusClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var veneer = new ChaosServiceBusClient(inner, Engine);
        Engine.Start();
        return veneer;
    }

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        .. base.DefaultMonkeyFaults(),
        Reason("CommunicationProblem", MonkeyFaultKind.Outage, ServiceBusFailureReason.ServiceCommunicationProblem, 2),
        Reason("ServiceBusy", MonkeyFaultKind.Error, ServiceBusFailureReason.ServiceBusy),
        Reason("Timeout", MonkeyFaultKind.Error, ServiceBusFailureReason.ServiceTimeout),
        Reason("LockLost", MonkeyFaultKind.Error, ServiceBusFailureReason.MessageLockLost),
        Reason("QuotaExceeded", MonkeyFaultKind.Error, ServiceBusFailureReason.QuotaExceeded),
        Reason("MessageSizeExceeded", MonkeyFaultKind.Weird, ServiceBusFailureReason.MessageSizeExceeded),
        new("Duplicate", MonkeyFaultKind.Weird, _ => DuplicateFault.Instance, 2),
        new("Drop", MonkeyFaultKind.DataLoss, _ => DropFault.Instance),
    ];

    private static MonkeyFault Reason(string name, MonkeyFaultKind kind, ServiceBusFailureReason reason, double weight = 1) =>
        new(name, kind, _ => new FailFault(call => ServiceBusFaults.Exception((ServiceBusChaosCall)call, reason, null)), weight);
}

/// <summary>Service Bus faults for a <see cref="ServiceBusFactory"/> window.</summary>
public static class ServiceBusFaults
{
    /// <summary>Each call fails with a <see cref="ServiceBusException"/> for the given reason.</summary>
    public static ServiceBusFactory Throw(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window, ServiceBusFailureReason reason, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(call => Exception(call, reason, message));
    }

    /// <summary>Each call fails with <see cref="ServiceBusFailureReason.QuotaExceeded"/>, as when a queue is full.</summary>
    public static ServiceBusFactory QuotaExceeded(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window) =>
        window.Throw(ServiceBusFailureReason.QuotaExceeded);

    /// <summary>
    /// Sent messages are sent twice, and processed messages run the handler twice, as at-least-once delivery allows.
    /// Other operations pass through.
    /// </summary>
    public static ServiceBusFactory Duplicate(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(DuplicateFault.Instance);
    }

    internal static ServiceBusException Exception(ServiceBusChaosCall call, ServiceBusFailureReason reason, string? message) => new(
        message ?? $"The {call.Operation} operation on '{call.EntityPath}' failed: {reason} (ChaosDotNet).",
        reason,
        call.EntityPath);

    /// <summary>Each call fails with a transient <see cref="ServiceBusFailureReason.ServiceBusy"/> error.</summary>
    public static ServiceBusFactory ServiceBusy(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window) =>
        window.Throw(ServiceBusFailureReason.ServiceBusy);

    /// <summary>Each call fails with a transient <see cref="ServiceBusFailureReason.ServiceTimeout"/> error.</summary>
    public static ServiceBusFactory Timeout(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window) =>
        window.Throw(ServiceBusFailureReason.ServiceTimeout);

    /// <summary>Each call fails with a transient <see cref="ServiceBusFailureReason.ServiceCommunicationProblem"/> error.</summary>
    public static ServiceBusFactory CommunicationProblem(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window) =>
        window.Throw(ServiceBusFailureReason.ServiceCommunicationProblem);

    /// <summary>Each call fails with <see cref="ServiceBusFailureReason.MessageLockLost"/>, as when a handler runs longer than the lock.</summary>
    public static ServiceBusFactory LockLost(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window) =>
        window.Throw(ServiceBusFailureReason.MessageLockLost);

    /// <summary>
    /// Sent and scheduled messages are accepted but never delivered, and processed messages skip the handler.
    /// Other operations pass through.
    /// </summary>
    public static ServiceBusFactory Drop(this WindowBuilder<ServiceBusFactory, ServiceBusChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(DropFault.Instance);
    }
}

/// <summary>A fault that delivers messages twice.</summary>
public sealed class DuplicateFault : Fault
{
    internal static readonly DuplicateFault Instance = new();

    private DuplicateFault()
    {
    }

    /// <inheritdoc />
    public override string Name => "Duplicate";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call.Operation is "Send" or "Process";
}

/// <summary>A fault that silently loses messages.</summary>
public sealed class DropFault : Fault
{
    internal static readonly DropFault Instance = new();

    private DropFault()
    {
    }

    /// <inheritdoc />
    public override string Name => "Drop";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call.Operation is "Send" or "Schedule" or "Process";
}

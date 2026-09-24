using System.Runtime.CompilerServices;
using Azure.Messaging.ServiceBus;
using ChaosDotNet.Factories;

namespace ChaosDotNet.AzureServiceBus;

internal sealed class ChaosServiceBusReceiver : ServiceBusReceiver
{
    private readonly ServiceBusReceiver _inner;
    private readonly ChaosEngine _engine;

    public ChaosServiceBusReceiver(ServiceBusReceiver inner, ChaosEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    public override string FullyQualifiedNamespace => _inner.FullyQualifiedNamespace;

    public override string EntityPath => _inner.EntityPath;

    public override ServiceBusReceiveMode ReceiveMode => _inner.ReceiveMode;

    public override int PrefetchCount => _inner.PrefetchCount;

    public override string Identifier => _inner.Identifier;

    public override bool IsClosed => _inner.IsClosed;

    public override Task CloseAsync(CancellationToken cancellationToken = default) => _inner.CloseAsync(cancellationToken);

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Receive"), () => _inner.ReceiveMessagesAsync(maxMessages, maxWaitTime, cancellationToken), cancellationToken);

    public override async IAsyncEnumerable<ServiceBusReceivedMessage> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _inner.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            await _engine.RunAsync(Call("Receive", message), () => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            yield return message;
        }
    }

    public override Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Receive"), () => _inner.ReceiveMessageAsync(maxWaitTime, cancellationToken), cancellationToken);

    public override Task<ServiceBusReceivedMessage> PeekMessageAsync(long? fromSequenceNumber = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Peek"), () => _inner.PeekMessageAsync(fromSequenceNumber, cancellationToken), cancellationToken);

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Peek"), () => _inner.PeekMessagesAsync(maxMessages, fromSequenceNumber, cancellationToken), cancellationToken);

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Complete", message), () => _inner.CompleteMessageAsync(message, cancellationToken), cancellationToken);

    public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Abandon", message), () => _inner.AbandonMessageAsync(message, propertiesToModify, cancellationToken), cancellationToken);

    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("DeadLetter", message), () => _inner.DeadLetterMessageAsync(message, propertiesToModify, cancellationToken), cancellationToken);

    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify, string deadLetterReason, string? deadLetterErrorDescription = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("DeadLetter", message), () => _inner.DeadLetterMessageAsync(message, propertiesToModify, deadLetterReason, deadLetterErrorDescription, cancellationToken), cancellationToken);

    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string? deadLetterErrorDescription = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("DeadLetter", message), () => _inner.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription, cancellationToken), cancellationToken);

    public override Task DeferMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("Defer", message), () => _inner.DeferMessageAsync(message, propertiesToModify, cancellationToken), cancellationToken);

    public override Task<ServiceBusReceivedMessage> ReceiveDeferredMessageAsync(long sequenceNumber, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("ReceiveDeferred"), () => _inner.ReceiveDeferredMessageAsync(sequenceNumber, cancellationToken), cancellationToken);

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveDeferredMessagesAsync(IEnumerable<long> sequenceNumbers, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("ReceiveDeferred"), () => _inner.ReceiveDeferredMessagesAsync(sequenceNumbers, cancellationToken), cancellationToken);

    public override Task RenewMessageLockAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("RenewLock", message), () => _inner.RenewMessageLockAsync(message, cancellationToken), cancellationToken);

    private ServiceBusChaosCall Call(string operation, ServiceBusReceivedMessage? message = null) =>
        new(operation, _inner.EntityPath, receivedMessage: message);
}

internal sealed class ChaosServiceBusProcessor : ServiceBusProcessor
{
    private readonly ChaosEngine _engine;

    public ChaosServiceBusProcessor(ServiceBusClient client, string queueName, ServiceBusProcessorOptions options, ChaosEngine engine)
        : base(client, queueName, options)
    {
        _engine = engine;
    }

    public ChaosServiceBusProcessor(ServiceBusClient client, string topicName, string subscriptionName, ServiceBusProcessorOptions options, ChaosEngine engine)
        : base(client, topicName, subscriptionName, options)
    {
        _engine = engine;
    }

    protected override async Task OnProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var call = new ServiceBusChaosCall("Process", EntityPath, receivedMessage: args.Message);
        if (await _engine.BeforeCallAsync(call, args.CancellationToken).ConfigureAwait(false) is DropFault)
        {
            return;
        }

        try
        {
            await base.OnProcessMessageAsync(args).ConfigureAwait(false);
            _engine.CallSucceeded(call);
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }
}

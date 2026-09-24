using Azure.Messaging.ServiceBus;
using ChaosDotNet.Factories;

namespace ChaosDotNet.AzureServiceBus;

internal sealed class ChaosServiceBusClient : ServiceBusClient
{
    private readonly ServiceBusClient _inner;
    private readonly ChaosEngine _engine;

    public ChaosServiceBusClient(ServiceBusClient inner, ChaosEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    public override string FullyQualifiedNamespace => _inner.FullyQualifiedNamespace;

    public override bool IsClosed => _inner.IsClosed;

    public override string Identifier => _inner.Identifier;

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    public override ServiceBusSender CreateSender(string queueOrTopicName) =>
        new ChaosServiceBusSender(_inner.CreateSender(queueOrTopicName), _engine);

    public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options) =>
        new ChaosServiceBusSender(_inner.CreateSender(queueOrTopicName, options), _engine);

    public override ServiceBusReceiver CreateReceiver(string queueName) =>
        new ChaosServiceBusReceiver(_inner.CreateReceiver(queueName), _engine);

    public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options) =>
        new ChaosServiceBusReceiver(_inner.CreateReceiver(queueName, options), _engine);

    public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName) =>
        new ChaosServiceBusReceiver(_inner.CreateReceiver(topicName, subscriptionName), _engine);

    public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options) =>
        new ChaosServiceBusReceiver(_inner.CreateReceiver(topicName, subscriptionName, options), _engine);

    public override ServiceBusProcessor CreateProcessor(string queueName) =>
        new ChaosServiceBusProcessor(_inner, queueName, new ServiceBusProcessorOptions(), _engine);

    public override ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options) =>
        new ChaosServiceBusProcessor(_inner, queueName, options, _engine);

    public override ServiceBusProcessor CreateProcessor(string topicName, string subscriptionName) =>
        new ChaosServiceBusProcessor(_inner, topicName, subscriptionName, new ServiceBusProcessorOptions(), _engine);

    public override ServiceBusProcessor CreateProcessor(string topicName, string subscriptionName, ServiceBusProcessorOptions options) =>
        new ChaosServiceBusProcessor(_inner, topicName, subscriptionName, options, _engine);

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string queueName, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.AcceptNextSessionAsync(queueName, options, cancellationToken);

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string topicName, string subscriptionName, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.AcceptNextSessionAsync(topicName, subscriptionName, options, cancellationToken);

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string queueName, string sessionId, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.AcceptSessionAsync(queueName, sessionId, options, cancellationToken);

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string topicName, string subscriptionName, string sessionId, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.AcceptSessionAsync(topicName, subscriptionName, sessionId, options, cancellationToken);

    public override ServiceBusSessionProcessor CreateSessionProcessor(string queueName, ServiceBusSessionProcessorOptions? options = null) =>
        _inner.CreateSessionProcessor(queueName, options);

    public override ServiceBusSessionProcessor CreateSessionProcessor(string topicName, string subscriptionName, ServiceBusSessionProcessorOptions? options = null) =>
        _inner.CreateSessionProcessor(topicName, subscriptionName, options);

    public override ServiceBusRuleManager CreateRuleManager(string topicName, string subscriptionName) =>
        _inner.CreateRuleManager(topicName, subscriptionName);
}

internal sealed class ChaosServiceBusSender : ServiceBusSender
{
    private readonly ServiceBusSender _inner;
    private readonly ChaosEngine _engine;

    public ChaosServiceBusSender(ServiceBusSender inner, ChaosEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    public override string FullyQualifiedNamespace => _inner.FullyQualifiedNamespace;

    public override string EntityPath => _inner.EntityPath;

    public override bool IsClosed => _inner.IsClosed;

    public override string Identifier => _inner.Identifier;

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) =>
        SendAsync([message], () => _inner.SendMessageAsync(message, cancellationToken), cancellationToken);

    public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        return SendAsync(list, () => _inner.SendMessagesAsync(list, cancellationToken), cancellationToken);
    }

    public override Task SendMessagesAsync(ServiceBusMessageBatch messageBatch, CancellationToken cancellationToken = default) =>
        SendAsync([], () => _inner.SendMessagesAsync(messageBatch, cancellationToken), cancellationToken);

    public override ValueTask<ServiceBusMessageBatch> CreateMessageBatchAsync(CancellationToken cancellationToken = default) =>
        _inner.CreateMessageBatchAsync(cancellationToken);

    public override ValueTask<ServiceBusMessageBatch> CreateMessageBatchAsync(CreateMessageBatchOptions options, CancellationToken cancellationToken = default) =>
        _inner.CreateMessageBatchAsync(options, cancellationToken);

    public override async Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
        (await ScheduleAsync([message], async () => [await _inner.ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken).ConfigureAwait(false)], cancellationToken).ConfigureAwait(false))[0];

    public override Task<IReadOnlyList<long>> ScheduleMessagesAsync(IEnumerable<ServiceBusMessage> messages, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        return ScheduleAsync(list, () => _inner.ScheduleMessagesAsync(list, scheduledEnqueueTime, cancellationToken), cancellationToken);
    }

    public override Task CancelScheduledMessageAsync(long sequenceNumber, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("CancelScheduled"), () => _inner.CancelScheduledMessageAsync(sequenceNumber, cancellationToken), cancellationToken);

    public override Task CancelScheduledMessagesAsync(IEnumerable<long> sequenceNumbers, CancellationToken cancellationToken = default) =>
        _engine.RunAsync(Call("CancelScheduled"), () => _inner.CancelScheduledMessagesAsync(sequenceNumbers, cancellationToken), cancellationToken);

    public override Task CloseAsync(CancellationToken cancellationToken = default) => _inner.CloseAsync(cancellationToken);

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private ServiceBusChaosCall Call(string operation, IReadOnlyList<ServiceBusMessage>? messages = null) =>
        new(operation, _inner.EntityPath, messages);

    private async Task SendAsync(IReadOnlyList<ServiceBusMessage> messages, Func<Task> send, CancellationToken cancellationToken)
    {
        var call = Call("Send", messages);
        var fault = await _engine.BeforeCallAsync(call, cancellationToken).ConfigureAwait(false);
        if (fault is DropFault)
        {
            return;
        }

        await Complete(call, async () =>
        {
            await send().ConfigureAwait(false);
            if (fault is DuplicateFault)
            {
                await send().ConfigureAwait(false);
            }

            return true;
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<long>> ScheduleAsync(IReadOnlyList<ServiceBusMessage> messages, Func<Task<IReadOnlyList<long>>> schedule, CancellationToken cancellationToken)
    {
        var call = Call("Schedule", messages);
        if (await _engine.BeforeCallAsync(call, cancellationToken).ConfigureAwait(false) is DropFault)
        {
            return messages.Select(_ => 0L).ToArray();
        }

        return await Complete(call, schedule).ConfigureAwait(false);
    }

    private async Task<T> Complete<T>(ServiceBusChaosCall call, Func<Task<T>> realCall)
    {
        try
        {
            var result = await realCall().ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return result;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }
}

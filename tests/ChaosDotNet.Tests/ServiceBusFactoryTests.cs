using System.Reflection;
using Azure.Messaging.ServiceBus;

namespace ChaosDotNet.Tests;

public sealed class ServiceBusFactoryTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeClient _inner = new();

    [Fact]
    public async Task Sends_pass_through()
    {
        await using var client = new ServiceBusFactory(_clock).CreateClient(_inner);
        var sender = client.CreateSender("orders");

        await sender.SendMessageAsync(new ServiceBusMessage("a"), TestContext.Current.CancellationToken);
        await sender.SendMessagesAsync([new ServiceBusMessage("b"), new ServiceBusMessage("c")], TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c"], _inner.Sender.Sent.Select(m => m.Body.ToString()));
        Assert.Equal("orders", sender.EntityPath);
    }

    [Fact]
    public async Task ServiceBusy_fails_sends_with_a_transient_service_bus_exception()
    {
        await using var client = new ServiceBusFactory(_clock).For(10, TimeUnit.Seconds).ServiceBusy().CreateClient(_inner);
        var sender = client.CreateSender("orders");

        var error = await Assert.ThrowsAsync<ServiceBusException>(() => sender.SendMessageAsync(new ServiceBusMessage("a"), TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await sender.SendMessageAsync(new ServiceBusMessage("b"), TestContext.Current.CancellationToken);

        Assert.Equal(ServiceBusFailureReason.ServiceBusy, error.Reason);
        Assert.True(error.IsTransient);
        Assert.Equal("orders", error.EntityPath);
        Assert.Equal(["b"], _inner.Sender.Sent.Select(m => m.Body.ToString()));
    }

    [Fact]
    public async Task Drop_accepts_sends_but_loses_the_messages()
    {
        var factory = new ServiceBusFactory(_clock).ForCalls(1).Drop();
        await using var client = factory.CreateClient(_inner);
        var sender = client.CreateSender("orders");

        await sender.SendMessageAsync(new ServiceBusMessage("lost"), TestContext.Current.CancellationToken);
        await sender.SendMessageAsync(new ServiceBusMessage("kept"), TestContext.Current.CancellationToken);
        var sequence = await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(["kept"], _inner.Sender.Sent.Select(m => m.Body.ToString()));
        Assert.Equal(7, sequence);
        factory.Verify().FaultsInjected(exactly: 1);
    }

    [Fact]
    public async Task Drop_ignores_receiver_operations()
    {
        await using var client = new ServiceBusFactory(_clock).Forever().Drop().CreateClient(_inner);
        var receiver = client.CreateReceiver("orders");

        var message = await receiver.ReceiveMessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        await receiver.CompleteMessageAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(1, _inner.Receiver.Completed);
    }

    [Fact]
    public async Task LockLost_fails_settlement()
    {
        await using var client = new ServiceBusFactory(_clock)
            .When(call => call.Operation == "Complete").ForCalls(1).LockLost()
            .CreateClient(_inner);
        var receiver = client.CreateReceiver("orders", "billing");

        var message = await receiver.ReceiveMessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ServiceBusException>(() => receiver.CompleteMessageAsync(message, TestContext.Current.CancellationToken));
        await receiver.CompleteMessageAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(ServiceBusFailureReason.MessageLockLost, error.Reason);
        Assert.Equal(1, _inner.Receiver.Completed);
    }

    [Fact]
    public async Task Receive_can_freeze()
    {
        await using var client = new ServiceBusFactory(_clock).For(5, TimeUnit.Seconds).Freeze().CreateClient(_inner);
        var receiver = client.CreateReceiver("orders");

        var receive = receiver.ReceiveMessagesAsync(10, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(receive.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Single(await receive);
    }

    [Fact]
    public async Task Streaming_receive_follows_the_timeline()
    {
        await using var client = new ServiceBusFactory(_clock).ForCalls(1).Timeout().CreateClient(_inner);
        var receiver = client.CreateReceiver("orders");

        await Assert.ThrowsAsync<ServiceBusException>(async () =>
        {
            await foreach (var _ in receiver.ReceiveMessagesAsync(TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task When_can_filter_on_message_content()
    {
        await using var client = new ServiceBusFactory(_clock)
            .When(call => call.Messages.Any(m => m.Subject == "payment"))
            .Forever().CommunicationProblem()
            .CreateClient(_inner);
        var sender = client.CreateSender("orders");

        await sender.SendMessageAsync(new ServiceBusMessage("x") { Subject = "shipping" }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ServiceBusException>(() => sender.SendMessageAsync(new ServiceBusMessage("y") { Subject = "payment" }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(typeof(ServiceBusClient), "ChaosDotNet.AzureServiceBus.ChaosServiceBusClient")]
    [InlineData(typeof(ServiceBusSender), "ChaosDotNet.AzureServiceBus.ChaosServiceBusSender")]
    [InlineData(typeof(ServiceBusReceiver), "ChaosDotNet.AzureServiceBus.ChaosServiceBusReceiver")]
    public void Veneers_override_every_public_virtual_member(Type sdkType, string veneerName)
    {
        var veneer = typeof(ServiceBusFactory).Assembly.GetType(veneerName, throwOnError: true)!;

        var missing = sdkType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.IsVirtual && !m.IsFinal && m.DeclaringType == sdkType)
            .Where(m => m.Name is not (nameof(Equals) or nameof(GetHashCode) or nameof(ToString)))
            .Where(m => veneer.GetMethod(m.Name, m.GetParameters().Select(p => p.ParameterType).ToArray())!.DeclaringType != veneer)
            .Select(m => m.ToString())
            .ToList();

        Assert.Empty(missing);
    }

    private sealed class FakeClient : ServiceBusClient
    {
        public FakeSender Sender { get; } = new();

        public FakeReceiver Receiver { get; } = new();

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            Sender.Path = queueOrTopicName;
            return Sender;
        }

        public override ServiceBusReceiver CreateReceiver(string queueName)
        {
            Receiver.Path = queueName;
            return Receiver;
        }

        public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName)
        {
            Receiver.Path = $"{topicName}/Subscriptions/{subscriptionName}";
            return Receiver;
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSender : ServiceBusSender
    {
        public string Path { get; set; } = string.Empty;

        public List<ServiceBusMessage> Sent { get; } = [];

        public override string EntityPath => Path;

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public override Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(7L);
    }

    private sealed class FakeReceiver : ServiceBusReceiver
    {
        public string Path { get; set; } = string.Empty;

        public int Completed { get; private set; }

        public override string EntityPath => Path;

        public override Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceBusModelFactory.ServiceBusReceivedMessage(new BinaryData("m")));

        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([ServiceBusModelFactory.ServiceBusReceivedMessage(new BinaryData("m"))]);

        public override async IAsyncEnumerable<ServiceBusReceivedMessage> ReceiveMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return ServiceBusModelFactory.ServiceBusReceivedMessage(new BinaryData("m"));
        }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            Completed++;
            return Task.CompletedTask;
        }
    }
}

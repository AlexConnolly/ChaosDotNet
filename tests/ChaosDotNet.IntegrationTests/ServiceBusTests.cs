using Azure.Messaging.ServiceBus;
using Testcontainers.ServiceBus;

namespace ChaosDotNet.IntegrationTests;

public sealed class ServiceBusFixture : ContainerFixture<ServiceBusContainer>
{
    protected override ServiceBusContainer Build() =>
        new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .Build();
}

public sealed class ServiceBusTests(ServiceBusFixture fixture) : IClassFixture<ServiceBusFixture>
{
    private const string Queue = "queue.1";

    [Fact]
    public async Task Dropped_sends_never_arrive()
    {
        fixture.SkipWithoutDocker();
        var factory = new ServiceBusFactory(new FakeTimeProvider()).ForCalls(1).Drop();
        await using var client = factory.CreateClient(new ServiceBusClient(fixture.Container.GetConnectionString()));
        await using var sender = client.CreateSender(Queue);
        await using var receiver = client.CreateReceiver(Queue);

        await sender.SendMessageAsync(new ServiceBusMessage("lost"), TestContext.Current.CancellationToken);
        await sender.SendMessageAsync(new ServiceBusMessage("kept"), TestContext.Current.CancellationToken);
        var received = await receiver.ReceiveMessagesAsync(10, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        foreach (var message in received)
        {
            await receiver.CompleteMessageAsync(message, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["kept"], received.Select(m => m.Body.ToString()));
    }

    [Fact]
    public async Task Processor_handler_failure_redelivers_the_message()
    {
        fixture.SkipWithoutDocker();
        var factory = new ServiceBusFactory(new FakeTimeProvider())
            .When(call => call.Operation == "Process").ForCalls(1).Fail(() => new InvalidOperationException("handler crashed"));
        await using var client = factory.CreateClient(new ServiceBusClient(fixture.Container.GetConnectionString()));
        await using var sender = client.CreateSender(Queue);
        await using var processor = client.CreateProcessor(Queue, new ServiceBusProcessorOptions { MaxConcurrentCalls = 1 });
        var handled = new TaskCompletionSource<ServiceBusReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        processor.ProcessMessageAsync += args =>
        {
            handled.TrySetResult(args.Message);
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += args =>
        {
            errors.Add(args.Exception);
            return Task.CompletedTask;
        };

        await sender.SendMessageAsync(new ServiceBusMessage("order-1"), TestContext.Current.CancellationToken);
        await processor.StartProcessingAsync(TestContext.Current.CancellationToken);
        var message = await handled.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await processor.StopProcessingAsync(TestContext.Current.CancellationToken);

        Assert.Equal("order-1", message.Body.ToString());
        Assert.Equal(2, message.DeliveryCount);
        Assert.Contains(errors, e => e.Message == "handler crashed");
        factory.Verify().FaultsInjected(exactly: 1).CallsSucceeded(atLeast: 2);
    }
}

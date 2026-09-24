# ChaosDotNet.AzureServiceBus

`ServiceBusFactory`: chaos for Azure Service Bus.

```shell
dotnet add package ChaosDotNet.AzureServiceBus
```

```csharp
var bus = new ServiceBusFactory()
    .When(call => call.Operation == "Send").For(30, TimeUnit.Seconds).ServiceBusy()
    .Then().When(call => call.Operation == "Process").ForCalls(1).Duplicate();

await using ServiceBusClient client = bus.CreateClient(new ServiceBusClient(connectionString));
```

Senders, receivers and processors created from the veneer follow the timeline. Session receivers, session processors and rule managers pass through.

`When(...)` gets a `ServiceBusChaosCall` with `EntityPath`, `Messages` (for sends) and `ReceivedMessage`. `Operation` is `Send`, `Schedule`, `CancelScheduled`, `Receive`, `ReceiveDeferred`, `Peek`, `Complete`, `Abandon`, `DeadLetter`, `Defer`, `RenewLock` or `Process` (a processor's message handler).

## Faults

| Fault | Effect |
| --- | --- |
| `ServiceBusy()`, `Timeout()`, `CommunicationProblem()` | Transient `ServiceBusException`s |
| `LockLost()` | `MessageLockLost`, as when a handler runs longer than the lock |
| `QuotaExceeded()` | The queue is full |
| `Throw(reason)` | Any `ServiceBusFailureReason` |
| `Duplicate()` | Sends go out twice; the processor runs the handler twice |
| `Drop()` | Sends succeed but the messages are lost; the processor skips the handler |

Plus `Freeze()`, `Latency(...)`, `Jitter(...)`, `Fail(...)` and `FailRandomly(...)` from the core. A `Fail` on `Process` makes the handler throw, so Service Bus abandons and redelivers the message.

## Chaos monkey

The catalogue is communication problems, busy, timeouts, lost locks, full queues, oversized messages, duplicates, freeze, latency and jitter. `Drop` is only used with `AllowDataLoss`.

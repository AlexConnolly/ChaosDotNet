# ChaosDotNet

Chaotic veneers over infrastructure clients for .NET tests.

Configure a factory with a timeline of faults, then create a normal client from it. The client behaves like the real one, except when the timeline says otherwise.

```csharp
using ChaosDotNet;
using ChaosDotNet.Factories;

var payments = new HttpFactory()
    .For(10, TimeUnit.Seconds).Freeze()
    .Then().ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 2);

HttpClient client = payments.CreateClient(new Uri("https://payments.test"));

// Pass the client to the code under test, run it, then check what happened.
payments.Verify().FaultsInjected(atLeast: 1).RecoveredWithin(5, TimeUnit.Seconds);
```

## Why

[Polly](https://www.pollydocs.org/chaos/) can inject faults into calls that go through a resilience pipeline, at a random rate. ChaosDotNet does something different:

| | Polly chaos strategies | ChaosDotNet |
| --- | --- | --- |
| When faults happen | Random, per call (`InjectionRate`) | On a timeline: "freeze for 10 s, then fail 3 calls, then recover" |
| Where | Inside a Polly pipeline | In the client itself (`HttpClient`, `DbConnection`, Redis, Service Bus, any interface) |
| Faults | Generic exception, latency, outcome | Real client exceptions: `SqlException` 1205, `RedisTimeoutException`, `ServiceBusException`, HTTP 503 with `Retry-After` |
| Made for | Production and tests | Tests |

Use both: `ChaosDotNet.Polly` puts a ChaosDotNet timeline inside a Polly pipeline.

## Packages

| Package | Factory | Creates |
| --- | --- | --- |
| `ChaosDotNet` | `HttpFactory`, `ProxyFactory<T>` | `HttpClient`, `DelegatingHandler`, any interface |
| `ChaosDotNet.Sql` | `SqlFactory` | `DbConnection`, `DbDataSource` (ADO.NET, Dapper) |
| `ChaosDotNet.EntityFrameworkCore` | `SqlFactory.CreateInterceptor()` | EF Core interceptor |
| `ChaosDotNet.SqlServer` | `SqlServerFaults` | Real `SqlException` instances |
| `ChaosDotNet.Npgsql` | `NpgsqlFaults` | Real `PostgresException` instances |
| `ChaosDotNet.Redis` | `RedisFactory` | `IConnectionMultiplexer` |
| `ChaosDotNet.Caching` | `CacheFactory` | `IDistributedCache` |
| `ChaosDotNet.AzureServiceBus` | `ServiceBusFactory` | `ServiceBusClient` (senders, receivers, processors) |
| `ChaosDotNet.Polly` | `PollyFactory` | Polly v8 strategy |

All packages target .NET 8 and .NET 10.

## The timeline

A factory holds a list of windows. Each window has one fault. Windows run one after another.

| Method | Meaning |
| --- | --- |
| `For(10, TimeUnit.Seconds)` | A window that lasts 10 seconds |
| `ForCalls(3)` | A window that lasts for the next 3 matching calls |
| `Until(() => healthy)` | A window that lasts until a condition is true |
| `Forever()` | A window that never ends |
| `Every(30, TimeUnit.Seconds).For(5, TimeUnit.Seconds)` | 5 seconds of fault every 30 seconds, forever |
| `After(5, TimeUnit.Seconds)` | 5 seconds of normal behaviour before the next window |
| `Then()` | Nothing. Makes chains easier to read |
| `When(call => ...)` | The next window applies only to matching calls. Others pass through and do not count |
| `.Flaky(0.2)` | The last window applies to about 20% of its calls, chosen by the seed |

After the last window, calls pass through to the real client.

The timeline starts on the first `Create...` call. Call `Start()` to start it at an exact moment.

### Faults

| Fault | Effect |
| --- | --- |
| `Freeze()` | The call waits until the window ends, then continues. The call's `CancellationToken` still works. |
| `Latency(2, TimeUnit.Seconds)` | The call waits 2 seconds, then continues |
| `Jitter(1, 3, TimeUnit.Seconds)` | The call waits a random 1–3 seconds, then continues |
| `Fail(() => new MyException())` | The call throws. A new exception is built for each call |
| `Fail<TimeoutException>()` | The call throws a new `TimeoutException` |

Each factory adds its own faults:

| Factory | Faults |
| --- | --- |
| `HttpFactory` | `Respond(status, retryAfterSeconds)`, `Respond(request => response)`, `Timeout()`, `ConnectionRefused()`, `ConnectionClosed()` |
| `SqlFactory` | `CommandTimeout()`, `ConnectionFailure()`, plus `Fail(SqlServerFaults.Deadlock)` or `Fail(NpgsqlFaults.Deadlock)` |
| `RedisFactory` | `Timeout()`, `ConnectionFailure()` |
| `CacheFactory` | `Miss()`, `Timeout()` |
| `ServiceBusFactory` | `ServiceBusy()`, `Timeout()`, `CommunicationProblem()`, `LockLost()`, `Throw(reason)`, `Drop()` |

`SqlServerFaults`: `Deadlock` (1205), `Timeout` (-2), `DatabaseUnavailable` (40613), `ServiceBusy` (40501), `ConnectionReset` (10054), `CannotOpenDatabase` (4060), `UniqueConstraintViolation` (2627), or `Create(number, message)`.

`NpgsqlFaults`: `Deadlock` (40P01), `SerializationFailure` (40001), `AdminShutdown` (57P01), `TooManyConnections` (53300), `StatementTimeout` (57014), `UniqueViolation` (23505), `ConnectionLost`, `Timeout`, or `Create(sqlState, message)`.

## Examples

### SQL with EF Core

```csharp
var orders = new SqlFactory()
    .When(call => call.CommandText?.Contains("INSERT") == true)
    .ForCalls(2).Fail(SqlServerFaults.Deadlock);

services.AddDbContext<OrdersDb>(o => o
    .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
    .AddInterceptors(orders.CreateInterceptor()));

// ... save an order ...

orders.Verify().FaultsInjected(exactly: 2);
```

### SQL with ADO.NET or Dapper

```csharp
var db = new SqlFactory().After(5, TimeUnit.Seconds).For(20, TimeUnit.Seconds).CommandTimeout();

await using DbConnection connection = db.CreateConnection(new SqlConnection(connectionString));
var skus = await connection.QueryAsync<string>("SELECT Sku FROM Orders");
```

`CreateDataSource(NpgsqlDataSource.Create(...))` does the same for a `DbDataSource`.

### Redis

```csharp
var redis = new RedisFactory()
    .When(call => call.Key?.StartsWith("session:") == true)
    .For(30, TimeUnit.Seconds).Timeout();

IConnectionMultiplexer multiplexer = redis.Create(await ConnectionMultiplexer.ConnectAsync("localhost"));
```

Commands on `GetDatabase()` and `GetSubscriber()` follow the timeline. Batches and transactions pass through.

### Azure Service Bus

```csharp
var bus = new ServiceBusFactory()
    .When(call => call.Operation == "Send").ForCalls(1).Drop()
    .Then().When(call => call.Operation == "Process").ForCalls(1).Fail<InvalidOperationException>();

await using ServiceBusClient client = bus.CreateClient(new ServiceBusClient(connectionString));
```

Operations: `Send`, `Schedule`, `CancelScheduled`, `Receive`, `ReceiveDeferred`, `Peek`, `Complete`, `Abandon`, `DeadLetter`, `Defer`, `RenewLock`, `Process` (a processor's message handler). Session receivers, session processors and rule managers pass through.

### Any interface

```csharp
var gateway = new ProxyFactory<IPaymentGateway>()
    .Every(1, TimeUnit.Minutes).For(10, TimeUnit.Seconds).Latency(3, TimeUnit.Seconds);

IPaymentGateway veneer = gateway.Create(new StripePaymentGateway(...));
```

### Polly

```csharp
var chaos = new PollyFactory().For(10, TimeUnit.Seconds).Fail<HttpRequestException>();

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions())
    .AddChaosTimeline(chaos)
    .Build();
```

### Dependency injection and WebApplicationFactory

Register veneers where the real clients go, for example in `ConfigureTestServices`:

```csharp
services.AddHttpClient("payments").AddHttpMessageHandler(() => payments.CreateHandler());
services.AddDbContext<OrdersDb>(o => o.UseSqlServer(cs).AddInterceptors(orders.CreateInterceptor()));
services.AddSingleton<IConnectionMultiplexer>(_ => redis.Create(realMultiplexer));
services.AddSingleton<IDistributedCache>(_ => cache.Create(realCache));
```

## Deterministic tests

Pass a `TimeProvider` and a seed. With `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) a 10-minute scenario runs in milliseconds:

```csharp
var clock = new FakeTimeProvider();
var factory = new HttpFactory(clock, seed: 42).For(10, TimeUnit.Minutes).Freeze();
var client = factory.CreateClient(baseAddress, innerHandler);

var request = client.GetAsync("/orders");
clock.Advance(TimeSpan.FromMinutes(10));
await request;
```

The same seed and the same calls give the same `Flaky` and `Jitter` results. `factory.Seed` shows the seed in use when you did not pass one.

To run several factories on one clock and start them together, use a scenario:

```csharp
var scenario = new ChaosScenario(clock, seed: 42);
var http = new HttpFactory(scenario).For(10, TimeUnit.Seconds).Freeze();
var sql = new SqlFactory(scenario).After(5, TimeUnit.Seconds).ForCalls(1).Fail(SqlServerFaults.Deadlock);
scenario.Start();
```

Each test builds its own factories, so parallel tests never share chaos.

## Verify

```csharp
factory.Verify()
    .FaultsInjected(atLeast: 1)          // or atMost, exactly, window: 0
    .CallsSucceeded(atLeast: 1)
    .RecoveredWithin(2, TimeUnit.Seconds) // first success after the last faulted window ended
    .NoCallsFrozen();
```

A failed check throws `ChaosAssertionException` with the whole timeline in the message. The log is also in `factory.Log`.

## Freeze limits

A `Freeze()` in a `ForCalls`, `Until` or `Forever` window has no end time. Such calls throw `ChaosFreezeTimeoutException` after 60 seconds on the factory's clock. Change the limit with `WithMaxFreeze(amount, unit)`.

## Your own factory

Derive from `ChaosFactory<TSelf, TCall>` and call `Engine.BeforeCallAsync(call)` (or `Engine.RunAsync(call, realCall)`) in your veneer. Add faults as extension methods on `WindowBuilder<TSelf, TCall>` that call `Inject(fault)`. See `HttpFactory` for a short example.

## Releasing

Push a `v*` tag (for example `v0.1.0-preview`). The Release workflow builds, tests and publishes to NuGet with Trusted Publishing, so no API key is stored in the repo.

## Building

```shell
dotnet build
dotnet test --solution ChaosDotNet.slnx
```

The integration tests need Docker. Without it they skip.

## Licence

MIT

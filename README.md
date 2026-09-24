# ChaosDotNet

Break your infrastructure on purpose, inside your .NET tests, and check that your code copes.

```csharp
var payments = new HttpFactory()
    .For(10, TimeUnit.Seconds).Freeze()
    .Then().ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable);

HttpClient client = payments.CreateClient(new Uri("https://payments.test"));
// Give the client to the code under test, run it, then:
payments.Verify().RecoveredWithin(5, TimeUnit.Seconds);
```

Or let a monkey break everything at random, repeatably:

```csharp
await ChaosMonkey.ExploreAsync(runs: 50, async monkey =>
{
    var orders = new SqlFactory(monkey).Named("orders");
    var payments = new HttpFactory(monkey).Named("payments");
    // ... build the app with them, then:
    await monkey.RunAsync(() => PlaceOrders(app));
    Assert.Equal(charges, orders);   // an invariant that must hold whatever breaks
});
```

```
Seed 3: Expected: 30  Actual: 29
Smallest failing plan (1 of 6 incident(s)):
  #3 00:26.5-00:31.6  orders Error: RandomTransient (43 % of calls)
Reproduce: set CHAOS_SEED=3 CHAOS_INCIDENTS=3
```

## Add chaos to what you already have

In an ASP.NET Core test, one line wraps every supported client the app registers:

```csharp
services.AddChaosMonkey(monkey, chaos => chaos.Http().EntityFrameworkCore().Redis().Clock().Interface<IPaymentGateway>());
```

Or wrap instances yourself. Every factory wraps an instance you already have. The instance keeps working as before; the chaos runs around each call.

```csharp
var monkey = new ChaosMonkey();

DbConnection connection = new SqlFactory(monkey).Named("orders").CreateConnection(myConnection);
HttpClient http = new HttpFactory(monkey).Named("payments").CreateClient(inner: myHandler);
IPaymentGateway gateway = new ProxyFactory<IPaymentGateway>(monkey).Named("gateway").Create(myGateway);

await monkey.RunAsync(() => RunTheApp(connection, http, gateway));
```

Use a factory without a monkey to script the chaos instead, or `new ChaosSubject<T>().Setup(...).Create(myInstance)` to override some methods and pass the rest to your instance.

## Why

Code that talks to databases, queues, caches and APIs usually has retry, timeout and fallback logic. That logic rarely runs in tests, because the dependencies never fail there. ChaosDotNet makes them fail: on a script you write, or at random like Netflix's Chaos Monkey, but from a seed, so every failure can be replayed.

Unlike Polly's chaos strategies (random, per call, inside a resilience pipeline), ChaosDotNet works on the clients themselves, follows a timeline, and throws the exceptions the real client would throw.

## Core concepts

| Concept | Meaning |
| --- | --- |
| **Factory** | One per dependency, for example `HttpFactory` or `SqlFactory`. Holds the timeline. |
| **Veneer** | What a factory's `Create...` method returns: a normal `HttpClient`, `DbConnection`, `IConnectionMultiplexer`, ... that follows the timeline. |
| **Timeline** | Windows in order: `For(10, TimeUnit.Seconds)`, `ForCalls(3)`, `After(...)`, `Every(...)`, `When(call => ...)`. After the last window, calls pass through. |
| **Fault** | What happens in a window: `Freeze()`, `Latency(...)`, `Fail(...)`, plus each factory's own, such as `RespondMalformedJson()` or `BreakReaderMidway()`. |
| **Verify** | `factory.Verify().FaultsInjected(...).RecoveredWithin(...)` checks what happened. |
| **Monkey** | Connects every factory and builds a random plan from a seed. `ExploreAsync` tries many seeds and shrinks failures. |
| **Subject** | `ChaosSubject<T>`: a chaotic mock of any interface. `Setup(...).Returns(...)` plus the same chaos. |
| **Clock** | `ClockFactory`: a chaotic `TimeProvider` for the app. Time jumps, goes backwards, drifts or stands still. |

Tests stay fast and repeatable: pass a `FakeTimeProvider` and a seed, and a 10-minute scenario runs in milliseconds.

## Packages

| Package | For | Read more |
| --- | --- | --- |
| [`ChaosDotNet`](https://www.nuget.org/packages/ChaosDotNet) | The core: timelines, faults, verify, monkey, `ChaosSubject<T>` mocks, `HttpFactory`, `ProxyFactory<T>` for any interface | [README](src/ChaosDotNet/README.md) |
| [`ChaosDotNet.Sql`](https://www.nuget.org/packages/ChaosDotNet.Sql) | `DbConnection` and `DbDataSource` (ADO.NET, Dapper) | [README](src/ChaosDotNet.Sql/README.md) |
| [`ChaosDotNet.EntityFrameworkCore`](https://www.nuget.org/packages/ChaosDotNet.EntityFrameworkCore) | EF Core interceptor | [README](src/ChaosDotNet.EntityFrameworkCore/README.md) |
| [`ChaosDotNet.SqlServer`](https://www.nuget.org/packages/ChaosDotNet.SqlServer) | Real `SqlException` faults | [README](src/ChaosDotNet.SqlServer/README.md) |
| [`ChaosDotNet.Npgsql`](https://www.nuget.org/packages/ChaosDotNet.Npgsql) | Real `PostgresException` faults | [README](src/ChaosDotNet.Npgsql/README.md) |
| [`ChaosDotNet.Redis`](https://www.nuget.org/packages/ChaosDotNet.Redis) | StackExchange.Redis | [README](src/ChaosDotNet.Redis/README.md) |
| [`ChaosDotNet.Caching`](https://www.nuget.org/packages/ChaosDotNet.Caching) | `IDistributedCache` | [README](src/ChaosDotNet.Caching/README.md) |
| [`ChaosDotNet.AzureServiceBus`](https://www.nuget.org/packages/ChaosDotNet.AzureServiceBus) | Azure Service Bus senders, receivers, processors | [README](src/ChaosDotNet.AzureServiceBus/README.md) |
| [`ChaosDotNet.Polly`](https://www.nuget.org/packages/ChaosDotNet.Polly) | A timeline inside a Polly v8 pipeline | [README](src/ChaosDotNet.Polly/README.md) |

All packages target .NET 8 and .NET 10. A full worked example is in [`samples/OrderService.Tests`](samples/OrderService.Tests).

## Building

```shell
dotnet build
dotnet test --solution ChaosDotNet.slnx
```

The integration tests need Docker; without it they skip. Releases publish to NuGet when a `v*` tag is pushed.

## Licence

MIT

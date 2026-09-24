# ChaosDotNet

The core package: timelines, faults, verify, the chaos monkey, `ChaosSubject<T>` for chaotic mocks, and two factories that need nothing else: `HttpFactory` and `ProxyFactory<T>`.

```shell
dotnet add package ChaosDotNet
```

## How it works

1. Make a factory.
2. Give it a timeline of windows. Each window has one fault.
3. Call a `Create...` method to get a veneer: a normal client that follows the timeline.
4. Give the veneer to the code under test.
5. Check what happened with `Verify()`.

```csharp
using ChaosDotNet;
using ChaosDotNet.Factories;

var payments = new HttpFactory()
    .For(10, TimeUnit.Seconds).Freeze()
    .Then().ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 2);

HttpClient client = payments.CreateClient(new Uri("https://payments.test"));
```

## Timeline

| Method | Window |
| --- | --- |
| `For(10, TimeUnit.Seconds)` | Lasts 10 seconds |
| `ForCalls(3)` | Lasts for the next 3 matching calls |
| `Until(() => healthy)` | Lasts until a condition is true |
| `Forever()` | Never ends |
| `Every(30, TimeUnit.Seconds).For(5, TimeUnit.Seconds)` | 5 seconds of fault every 30 seconds |
| `After(5, TimeUnit.Seconds)` | 5 seconds of normal calls first |
| `When(call => ...)` | The next window only affects matching calls |
| `.Flaky(0.2)` | The last window affects about 20% of its calls |
| `Then()` | Does nothing; reads well |

The timeline starts on the first `Create...` call, or on `Start()`. After the last window, calls pass through.

## Faults for every factory

| Fault | Effect |
| --- | --- |
| `Freeze()` | Waits until the window ends, then continues. The call's `CancellationToken` still works. |
| `Latency(2, TimeUnit.Seconds)` | Waits, then continues |
| `Jitter(1, 3, TimeUnit.Seconds)` | Waits a random time, then continues |
| `Fail(() => new MyException())` / `Fail<TimeoutException>()` | Throws; a new exception each call |
| `FailRandomly(() => new A(), () => new B())` | Throws one picked at random |

## HttpFactory

`CreateClient(baseAddress, inner)` returns an `HttpClient`. `CreateHandler()` returns a `DelegatingHandler` for `IHttpClientFactory`:

```csharp
services.AddHttpClient("payments").AddHttpMessageHandler(() => payments.CreateHandler());
```

| Fault | Effect |
| --- | --- |
| `Respond(status, retryAfterSeconds)` / `Respond(request => response)` | A fake response |
| `Timeout()`, `ConnectionRefused()`, `ConnectionClosed()` | The exceptions `HttpClient` really throws |
| `RandomNetworkError()` | Reset, refused, DNS, TLS, proxy or timeout, at random |
| `RespondUnexpectedStatus()` | 400, 401, 403, 404, 409, 418, 422, 429, 500, 502, 503, 504 or 507, at random |
| `RespondMalformedJson()` | `200 OK` with JSON cut off halfway |
| `RespondMaintenancePage()` | `200 OK` with an HTML "down for maintenance" page |
| `RespondProxyErrorPage()` | `502` with an nginx HTML page |
| `RespondEmpty()` | `200 OK` with an empty JSON body |
| `RespondGarbage()` | `200 OK` with random bytes |
| `BreakBodyMidway()` | Reaches the server, then the body stops halfway with an `IOException` |

## ProxyFactory&lt;T&gt;

Wraps any interface, for example your own `IPaymentGateway`:

```csharp
var gateway = new ProxyFactory<IPaymentGateway>().ForCalls(2).Fail<TimeoutException>();
IPaymentGateway veneer = gateway.Create(realGateway);
```

## ChaosSubject&lt;T&gt;

A chaotic mock of any interface. Set up what methods return, add chaos, and create it. No real implementation or mocking library needed.

```csharp
var gateway = new ChaosSubject<IPaymentGateway>()
    .Setup(g => g.ChargeAsync(Arg.Any<string>(), Arg.Is<decimal>(a => a > 0)))
        .Returns((string sku, decimal amount) => new Receipt(sku, amount))
    .Setup(g => g.RefundAsync(Arg.Any<string>())).Throws<NotSupportedException>()
    .When(g => g.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>()))
        .For(10, TimeUnit.Seconds).Freeze()
    .Then().ForCalls(3).ReturnOddValues();

IPaymentGateway payments = gateway.Create();
// ... run the code under test ...
gateway.Received(g => g.ChargeAsync("A-1", 10m), Times.AtLeast(1));
```

| API | Meaning |
| --- | --- |
| `Setup(t => t.Method(args))` | A setup for matching calls: methods and property getters. The last matching setup wins. |
| `.Returns(value)` / `.Returns((a, b) => ...)` | The result. On async methods the value is wrapped in a completed task. `ReturnsAsync` is an alias. |
| `.ReturnsInOrder(a, b, c)` | One value per call, then the last value again |
| `.Throws<T>()` / `.Throws(() => ex)` | Always throw. Async methods return a faulted task. |
| `.Callback((a, b) => ...)` | Run code on each call, before the result |
| `Arg.Any<T>()`, `Arg.Is<T>(predicate)`, literals | Argument matchers |
| `When(t => t.Method(args))` | The next chaos window only hits matching calls |
| `Received(expression, Times.Once)`, `DidNotReceive(expression)`, `Calls` | Check calls, including ones chaos failed |
| `new ChaosSubject<T>(strict: true)` | Calls with no setup throw, instead of returning defaults (empty strings and collections, completed tasks, `default`) |
| `Create(inner)` | A partial mock: calls with no setup go to a real object |

Chaos runs before behaviour: a fault that throws or freezes applies first, then the setup decides the result.

Already use Moq or NSubstitute? Keep them, and pass `mock.Object` to `ProxyFactory<T>` to add chaos.

## Odd return values

`ReturnOddValues()` makes methods return something valid but unexpected: `null`, `""`, a 10,000-character string, `NaN`, `MinValue` and `MaxValue`, undefined enum values, `Guid.Empty`, empty collections. It works on `ChaosSubject<T>` and `ProxyFactory<T>`, and is in both monkey catalogues, so the monkey tests null handling and validation, not just retries. `OddValues.For(type, random)` gives the same values for your own use.

## Verify

```csharp
factory.Verify()
    .FaultsInjected(atLeast: 1)          // or atMost, exactly, window: 0
    .CallsSucceeded(atLeast: 1)
    .RecoveredWithin(2, TimeUnit.Seconds)
    .NoCallsFrozen();
```

A failed check throws `ChaosAssertionException` with the whole timeline in the message. `factory.Log` has the same events.

## Repeatable tests

Pass a clock and a seed:

```csharp
var clock = new FakeTimeProvider();
var factory = new HttpFactory(clock, seed: 42).For(10, TimeUnit.Minutes).Freeze();
var request = factory.CreateClient(baseAddress, inner).GetAsync("/orders");
clock.Advance(TimeSpan.FromMinutes(10));
await request;
```

To start several factories together on one clock, pass a `ChaosScenario` to each and call `scenario.Start()`.

## Add the monkey to what you already have

Every factory wraps an instance you already have. The instance keeps working as before; the chaos runs around each call.

```csharp
var monkey = new ChaosMonkey();

DbConnection connection = new SqlFactory(monkey).Named("orders").CreateConnection(myConnection);
HttpClient http = new HttpFactory(monkey).Named("payments").CreateClient(inner: myHandler);
IPaymentGateway gateway = new ProxyFactory<IPaymentGateway>(monkey).Named("gateway").Create(myGateway);

await monkey.RunAsync(() => RunTheApp(connection, http, gateway));
```

Use a factory without a monkey to script the chaos instead, or `new ChaosSubject<T>().Setup(...).Create(myInstance)` to override some methods and pass the rest to your instance.

## Chaos monkey

A `ChaosMonkey` is a scenario that breaks its factories at random. It picks which dependencies break, when, how long for and how (outages, slowness, errors and odd responses), sometimes several at once. A seed decides the plan, so the same seed gives the same chaos.

```csharp
await ChaosMonkey.ExploreAsync(runs: 50, async monkey =>
{
    var payments = new HttpFactory(monkey).Named("payments");
    var gateway = new ProxyFactory<IPaymentGateway>(monkey).Named("gateway");
    // ... build the app with veneers from both ...
    await monkey.RunAsync(() => PlaceOrders(app));   // plays the plan on a fake clock
    Assert.Equal(charges, orders);                   // invariants, not exact outcomes
});
```

`ExploreAsync` runs the test with seeds 1 to N. Each failing seed is shrunk to the fewest incidents that still fail, and the message says how to replay it (`CHAOS_SEED=3 CHAOS_INCIDENTS=3`). Set `CHAOS_EXPLORE=random` to try new seeds, for example in a nightly build.

| Option | Default |
| --- | --- |
| `Duration` | 60 s |
| `Intensity` | `Medium` (`Low`, `Medium`, `High`) |
| `CorrelatedFaults` | On: some incidents hit several dependencies at once |
| `MaxConcurrentIncidents` | 2 |
| `AllowDataLoss` | Off: no `Drop` or `Miss` faults |

Each factory has a catalogue of faults the monkey can pick. Change it with `WithMonkeyFaults(...)` or `AddMonkeyFaults(...)`. Factories with hand-written windows are left alone.

`RunAsync` moves a fake clock like a simulation: when the workload has settled, it jumps to the next timer the workload waits on. So the same seed gives the same history on a fast laptop or a slow CI runner. Pace the workload on `monkey.Clock`, for example `await Task.Delay(TimeSpan.FromSeconds(2), monkey.Clock)`; timers on other clocks still work, but the monkey falls back to fixed steps. For real containers, set `RealTime = true` in `ChaosExploreOptions`.

## Limits

- A freeze in a `ForCalls`, `Until` or `Forever` window has no end, so it throws `ChaosFreezeTimeoutException` after 60 s on the factory's clock. Change it with `WithMaxFreeze(...)`.
- Only calls through a veneer are affected.

## Your own factory

Derive from `ChaosFactory<TSelf, TCall>`. In the veneer, call `Engine.RunAsync(call, realCall)`, or `Engine.BeforeCallAsync(call)` to handle your own fault types. Add faults as extension methods on `WindowBuilder<TSelf, TCall>` that call `Inject(fault)`, and override `DefaultMonkeyFaults()` to give the monkey a catalogue. `HttpFactory` is a short example.

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

## ClockFactory

A chaotic `TimeProvider` for the app itself, to catch bugs caused by time: expired tokens, broken schedules, negative durations.

```csharp
var clock = new ClockFactory()
    .After(10, TimeUnit.Seconds).For(30, TimeUnit.Seconds).JumpBackward(5, TimeUnit.Minutes)
    .Then().For(1, TimeUnit.Minutes).Drift(1.2);

services.AddSingleton<TimeProvider>(clock.Create());
```

| Fault | Effect while the window is active |
| --- | --- |
| `JumpForward(amount, unit)` / `JumpBackward(amount, unit)` | `GetUtcNow()` is ahead or behind |
| `Drift(factor)` | Time runs fast (`1.2`) or slow (`0.8`); timestamps drift too |
| `Stall()` | Time stands still |
| `DaylightSaving(hours)` | The local offset shifts; UTC is unchanged |
| `LateTimers(amount, unit)` | Timers and delays fire late |
| `NonMonotonic()` | `GetTimestamp()` can go backwards |

When a window ends the clock snaps back to real time, like an NTP correction. `GetTimestamp()` stays monotonic unless `NonMonotonic()` is on. Only code that uses the `TimeProvider` is affected; `DateTime.UtcNow` and `Stopwatch` cannot be intercepted.

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

## AddChaosMonkey

One line in the test's service setup wraps every supported client the app registered:

```csharp
using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
    services.AddChaosMonkey(monkey, chaos => chaos
        .Http()                        // every IHttpClientFactory client, innermost so resilience handlers see the faults
        .EntityFrameworkCore()         // every DbContext (ChaosDotNet.EntityFrameworkCore)
        .Redis()                       // IConnectionMultiplexer (ChaosDotNet.Redis)
        .DistributedCache()            // IDistributedCache (ChaosDotNet.Caching)
        .ServiceBus()                  // ServiceBusClient (ChaosDotNet.AzureServiceBus)
        .Clock()                       // the app's TimeProvider
        .Interface<IPaymentGateway>()  // any interface
        .Except("http:health"))));
```

Dependencies are named `http:{client}`, `sql:{context}`, `redis`, `cache`, `servicebus`, `clock` and the interface name. Lifetimes are kept. Call it after the app's own registrations; it throws if there is nothing to wrap. Named HTTP clients are found once they have any configuration (a base address or a handler); typed clients are always found.

`AddChaos(scenario, ...)` does the same with hand-written timelines, for example `chaos.Http((name, f) => f.ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable))`.

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

## Reports and coverage

Set a report folder and `ExploreAsync` (and `[ChaosTheory]`) writes an HTML page for each failing run:

```shell
CHAOS_REPORT_DIR=./chaos-reports dotnet test
```

Each page is one self-contained file: a timeline with a lane per dependency, the incidents as bars and every call as a dot (passed, fault injected, fault skipped, real client failed), then the plan and every event with its details (HTTP path, SQL text, cache key, ...). For a shrunk failure, the timeline shows the smallest failing replay and fades the incidents that were not needed. The failure message ends with the page's path.

`index.html` in the folder lists every failure, then coverage: for each dependency, which faults in its catalogue hit at least one call. A fault that was planned but never hit a call means the code did not use that dependency while it was broken. Coverage also breaks down per test.

| `ChaosExploreOptions` | Meaning |
| --- | --- |
| `ReportDirectory` | The folder. Defaults to `CHAOS_REPORT_DIR`. Nothing is written when neither is set. |
| `ReportAll` | Write a page for every run, not only failures. |
| `Name` | The test's name in titles and coverage. Defaults to the calling method. |

Coverage is kept per test assembly: each test run replaces that assembly's previous results. Read it in code with `ChaosCoverage.Load(folder)`, for example to fail a build on `coverage.Gaps`. For runs made another way, call `ChaosReports.Record(folder, name, monkey, failure)` or build one page with `ChaosReport.From(monkey, title).WriteHtml(path)`.

## Limits

- A freeze in a `ForCalls`, `Until` or `Forever` window has no end, so it throws `ChaosFreezeTimeoutException` after 60 s on the factory's clock. Change it with `WithMaxFreeze(...)`.
- Only calls through a veneer are affected.

## Your own factory

Derive from `ChaosFactory<TSelf, TCall>`. In the veneer, call `Engine.RunAsync(call, realCall)`, or `Engine.BeforeCallAsync(call)` to handle your own fault types. Add faults as extension methods on `WindowBuilder<TSelf, TCall>` that call `Inject(fault)`, and override `DefaultMonkeyFaults()` to give the monkey a catalogue. `HttpFactory` is a short example.

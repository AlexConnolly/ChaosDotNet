# ChaosDotNet.Polly

Put a ChaosDotNet timeline inside a Polly v8 resilience pipeline.

```shell
dotnet add package ChaosDotNet.Polly
```

```csharp
var chaos = new PollyFactory().For(10, TimeUnit.Seconds).Fail<HttpRequestException>();

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions())
    .AddChaosTimeline(chaos)
    .Build();
```

Strategies added before `AddChaosTimeline` (a retry, a circuit breaker) see its faults. `When(...)` gets a `PollyChaosCall`; `Operation` is the Polly operation key.

Polly's own chaos strategies inject faults at random, per call. Use this package when you want faults on a timeline ("fail for 10 seconds, then recover") or a chaos monkey plan.

## Chaos monkey

The catalogue is random timeouts, `HttpRequestException`s and `IOException`s, plus freeze, latency and jitter.

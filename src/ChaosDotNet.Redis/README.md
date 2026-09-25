# ChaosDotNet.Redis

`RedisFactory`: chaos for StackExchange.Redis.

```shell
dotnet add package ChaosDotNet.Redis
```

```csharp
var redis = new RedisFactory()
    .When(call => call.Key?.StartsWith("session:") == true)
    .For(30, TimeUnit.Seconds).Timeout();

IConnectionMultiplexer multiplexer = redis.Create(await ConnectionMultiplexer.ConnectAsync("localhost"));
```

Commands on `GetDatabase()` and `GetSubscriber()` follow the timeline. Batches and transactions pass through. `When(...)` gets a `RedisChaosCall` with `Key`, `Channel`, `Operation` (for example `StringGet`) and the arguments.

## Faults

| Fault | Effect |
| --- | --- |
| `Timeout()` | Throws a real `RedisTimeoutException` |
| `ConnectionFailure()` | Throws a real `RedisConnectionException` |
| `ServerError()` | Throws a `RedisServerException`: `LOADING`, `READONLY`, `BUSY`, `MASTERDOWN`, `OOM`, `CLUSTERDOWN` or `TRYAGAIN` |
| `CorruptValues()` | Single-value reads return random bytes |
| `Miss()` | Single-value reads return no value |

Plus `Freeze()`, `Latency(...)`, `Jitter(...)`, `Fail(...)` and `FailRandomly(...)` from the core.

## Dependency injection

`services.AddChaosMonkey(monkey, chaos => chaos.Redis())` wraps the registered `IConnectionMultiplexer`, named `redis`. `chaos.Redis(ChaosStrategy.Replace, "localhost:6379")` drops the app's multiplexer and connects a new one instead.

## Chaos monkey

The catalogue is connection failures, timeouts, server errors, corrupt values, freeze, latency and jitter. `Miss` is only used with `AllowDataLoss`.

# ChaosDotNet.Caching

`CacheFactory`: chaos for `IDistributedCache`. `HybridCache` and output caching use `IDistributedCache` underneath, so the veneer works under them too.

```shell
dotnet add package ChaosDotNet.Caching
```

```csharp
var cache = new CacheFactory().For(1, TimeUnit.Minutes).Miss();
services.AddSingleton<IDistributedCache>(_ => cache.Create(realCache));
```

`When(...)` gets a `CacheChaosCall` with `Key` and `Operation` (`Get`, `Set`, `Refresh` or `Remove`).

## Faults

| Fault | Effect |
| --- | --- |
| `Timeout()` | Every operation throws `TimeoutException` |
| `Miss()` | Reads return nothing; writes pass through |
| `Corrupt()` | Reads return random bytes; writes pass through |

Plus `Freeze()`, `Latency(...)`, `Jitter(...)`, `Fail(...)` and `FailRandomly(...)` from the core.

## Dependency injection

`services.AddChaosMonkey(monkey, chaos => chaos.DistributedCache())` wraps the registered `IDistributedCache`, named `cache`. `chaos.DistributedCache(ChaosStrategy.Replace)` drops the app's cache and uses an in-memory one instead.

## Chaos monkey

The catalogue is timeouts, corrupt reads, freeze, latency and jitter. `Miss` is only used with `AllowDataLoss`.

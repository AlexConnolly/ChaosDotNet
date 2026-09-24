# ChaosDotNet.Xunit

`[ChaosTheory]` for xUnit v3: a chaos monkey test that runs once per seed, each seed as its own test case.

```shell
dotnet add package ChaosDotNet.Xunit
```

```csharp
[ChaosTheory(Runs = 50)]
public async Task Orders_are_never_lost(ChaosMonkey monkey)
{
    using var app = CreateApp(services => services.AddChaosMonkey(monkey, chaos => chaos.Http().EntityFrameworkCore()));
    await monkey.RunAsync(() => PlaceOrders(app));
    Assert.Equal(await Charges(app), await Orders(app));
}
```

The test explorer shows one case per seed:

```
Orders_are_never_lost(seed: 1)   passed
Orders_are_never_lost(seed: 2)   passed
Orders_are_never_lost(seed: 3)   failed
```

A failing case re-runs itself with fewer incidents until it finds the smallest plan that still fails, and adds it to the failure message:

```
System.InvalidOperationException : stock is down
Chaos seed 3. Smallest failing plan (1 of 6 incident(s)):
  #6 00:57.9-01:00.0  stock Outage: Down (49 % of calls)
Reproduce: set CHAOS_SEED=3 CHAOS_INCIDENTS=6
```

## Rules

- The method takes exactly one `ChaosMonkey` parameter. Each case gets a new monkey with its seed and a fake clock (`RealTime = true` for the system clock).
- Seeds are 1 to `Runs` (default 20), so every build runs the same cases. `Seeds = [3, 17]` pins seeds that failed before as regression cases.
- `CHAOS_SEED=3` runs only seed 3, and `CHAOS_INCIDENTS=6` replays only those incidents. `CHAOS_EXPLORE=random` picks new seeds, for example in a nightly build.
- Shrinking re-runs the whole test silently (constructor, before/after attributes, body), up to `MaxShrinkRuns` times. Set `Shrink = false` if the test has side effects that must not repeat.
- With `CHAOS_REPORT_DIR` set (or `ReportDirectory = "..."`), each failing case writes an HTML report and adds `Report: <path>` to its message. Every case adds to the folder's coverage in `index.html`. `ReportAll = true` writes a page for passing cases too. See [Reports and coverage](https://github.com/AlexConnolly/ChaosDotNet/blob/main/src/ChaosDotNet/README.md#reports-and-coverage).
- Other options: `Intensity`, `DurationSeconds`, `CorrelatedFaults`, `MaxConcurrentIncidents`, `AllowDataLoss`, `Shrink` (default on), `MaxShrinkRuns` (default 30). `Skip`, `Explicit`, `Timeout` and traits work as on `[Fact]`.

For other test frameworks, use `ChaosMonkey.ExploreAsync` from the core package.

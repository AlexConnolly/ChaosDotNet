# ChaosDotNet.Cli

`dotnet chaos test` runs the test suite you already have with a chaos monkey inside it. No test changes.

## Install

It is a separate package from the libraries: a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools). Your test projects do not need to reference anything.

For one repo, so everyone on the team and CI get the same version (recommended):

```shell
dotnet new tool-manifest            # once per repo; commit the dotnet-tools.json it creates
dotnet tool install ChaosDotNet.Cli --prerelease
```

Others then run `dotnet tool restore`. Or, for every repo on your machine:

```shell
dotnet tool install --global ChaosDotNet.Cli --prerelease
```

`--prerelease` is needed while versions end in `-preview`. Update with `dotnet tool update ChaosDotNet.Cli --prerelease` (add `--global` for a global install).

Or, with no tool install: a test project that references the `ChaosDotNet` package already has the runner, as an MSBuild target:

```shell
dotnet build -t:ChaosTest -p:ChaosRuns=5 -p:ChaosArgs="--filter-namespace MyApp.Tests.Orders*"
```

`ChaosRuns`, `ChaosSeed`, `ChaosIntensity` and `ChaosServices=true` match the tool's options; `ChaosArgs` goes to `dotnet test`. Run it on a test project, not a solution.

## Run

```shell
dotnet chaos test --runs 5 --filter-namespace "MyApp.Tests.Orders*"
```

```
Seed 1: 1 test(s) failed only under chaos. Faults injected: http 14, sql 31.
  FAILED Orders.Tests.CheckoutTests.Order_is_saved_after_payment
    Error: ChaosDotNet.Factories.ChaosDbException : A transport-level error has occurred.
    Cause: an injected fault reached the test without being handled.
    Infra while it ran (09:59:39.978 to 09:59:40.876):
      #1 sql ConnectionFailure on 26 % of calls, 09:59:38.847 to 09:59:41.047
      #1 http Latency on all calls, 09:59:38.847 to 09:59:41.047
    Calls broken:
      sql ConnectionFailure (#1) x1 on ExecuteNonQuery: INSERT INTO "Orders" ...
Report: /tmp/dotnet-chaos-qquxcj3d/report.txt

dotnet-chaos: 1 test(s) pass without chaos but fail under it:
  Orders.Tests.CheckoutTests.Order_is_saved_after_payment  (seeds 1)
Reproduce: dotnet chaos test --seed 1 --intensity high --filter Category=Orders
```

Each failure shows:

- **Error**: the first line of the test's failure message.
- **Cause**: whether an injected fault reached the test unhandled, or the test failed on its own assertion after a fault changed what the code did.
- **Infra while it ran**: every incident active in the test's time window, with the fault and the share of calls it broke.
- **Calls broken**: the calls the monkey broke in that window, grouped by fault and operation, with the SQL or HTTP path.

Tests that run in parallel share one monkey, so a fault in a test's window may have hit another test's call.

## What it does

1. Runs `dotnet test` once without chaos.
2. Runs it once per seed with a startup hook (`DOTNET_STARTUP_HOOKS`) in every test process. The hook starts a `ChaosMonkey` with that seed and patches the process at run time:
   - every `SocketsHttpHandler`, so every `HttpClient` and `IHttpClientFactory` client that reaches the network (dependency `http`);
   - every ADO.NET `DbConnection` and `DbCommand` (dependency `sql`): SqlClient, Npgsql, SQLite, MySQL, EF Core, Dapper;
   - every TCP `Socket` the app opens (dependency `tcp`): connections are refused, reset or time out. This reaches clients with their own wire protocol, such as Redis, Service Bus, RabbitMQ, Mongo and gRPC. The test platform's own sockets are left alone.

   With `--services`, it also wraps the app's own interfaces in every DI container the tests build (`BuildServiceProvider`, so `WebApplicationFactory` and the generic host too). Each one throws, slows down or returns odd values, named after the interface. `Microsoft.*` and `System.*` services, open generics and keyed services are left alone.
3. Compares TRX results. For each test that passed without chaos but failed with it, it prints the error and the state of the infrastructure while the test ran, and writes it all to `report.txt`. Exit code 1 if there are any.

| Option | Meaning |
| --- | --- |
| `--runs <n>` | Seeds 1 to n. Defaults to 5. |
| `--seed <seed>` | One seed, to reproduce a failure. |
| `--intensity low\|medium\|high` | How much chaos. Defaults to `high`, so short suites see faults. |
| `--services` | Also break your own services in the DI container, not only infrastructure. Off by default: infrastructure failures are the ones that happen in production. |

Every other option goes to `dotnet test`, so the usual filters pick which tests run, in both the clean run and the chaos runs:

```shell
# xunit.v3 on Microsoft.Testing.Platform: a namespace and everything below it
dotnet chaos test --filter-namespace "MyApp.Tests.Orders*"

# VSTest (xunit v2, NUnit, MSTest)
dotnet chaos test --filter "FullyQualifiedName~MyApp.Tests.Orders."
```

## Limits

- The monkey runs on the real clock, so which calls a fault hits depends on timing. The same seed gives the same plan, not always the same failures.
- Database faults are provider-neutral `DbException`s (`IsTransient` is true). Retry strategies that check provider error numbers, such as EF Core's SQL Server strategy, do not retry them.
- Socket faults are failures only (refused, reset, timed out). A slow socket would block a thread, so slowness comes from the HTTP and database layers.
- `--services` only proxies interfaces. Concrete classes registered as themselves are left alone.
- In-memory handlers (for example `WebApplicationFactory`'s test server or a fake `HttpMessageHandler`) do not reach the network and get no chaos.
- With Microsoft.Testing.Platform it asks for TRX with `--report-trx`, then `--report-xunit-trx` for xunit.v3. Each test project needs one of them.

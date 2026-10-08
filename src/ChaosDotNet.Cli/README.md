# ChaosDotNet.Cli

`dotnet chaos test` runs the test suite you already have with a chaos monkey inside it. No test changes.

```shell
dotnet tool install --global ChaosDotNet.Cli
dotnet chaos test --runs 5 --filter "Category=Orders"
```

```
dotnet-chaos: baseline 120 passed, 0 failed
dotnet-chaos: seed 1: 2 failed only under chaos; faults injected: http 14, sql 31
...
dotnet-chaos: 2 test(s) pass without chaos but fail under it:
  Orders.Tests.CheckoutTests.Order_is_saved_after_payment  (seeds 1, 4)
  Orders.Tests.StockTests.Reservation_is_released  (seeds 1)
Reproduce: dotnet chaos test --seed 1 --intensity high --filter Category=Orders
```

## What it does

1. Runs `dotnet test` once without chaos.
2. Runs it once per seed with a startup hook (`DOTNET_STARTUP_HOOKS`) in every test process. The hook starts a `ChaosMonkey` with that seed and patches the process at run time:
   - every `SocketsHttpHandler`, so every `HttpClient` and `IHttpClientFactory` client that reaches the network (dependency `http`);
   - every ADO.NET `DbConnection` and `DbCommand` (dependency `sql`): SqlClient, Npgsql, SQLite, MySQL, EF Core, Dapper.
3. Compares TRX results and lists tests that passed without chaos but failed with it. Exit code 1 if there are any.

| Option | Meaning |
| --- | --- |
| `--runs <n>` | Seeds 1 to n. Defaults to 5. |
| `--seed <seed>` | One seed, to reproduce a failure. |
| `--intensity low\|medium\|high` | How much chaos. Defaults to `high`, so short suites see faults. |

Every other option goes to `dotnet test`.

## Limits

- The monkey runs on the real clock, so which calls a fault hits depends on timing. The same seed gives the same plan, not always the same failures.
- Database faults are provider-neutral `DbException`s (`IsTransient` is true). Retry strategies that check provider error numbers, such as EF Core's SQL Server strategy, do not retry them.
- In-memory handlers (for example `WebApplicationFactory`'s test server or a fake `HttpMessageHandler`) do not reach the network and get no chaos.
- With Microsoft.Testing.Platform it asks for TRX with `--report-trx`, then `--report-xunit-trx` for xunit.v3. Each test project needs one of them.

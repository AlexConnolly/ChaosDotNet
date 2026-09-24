# ChaosDotNet.Sql

`SqlFactory`: chaos for any ADO.NET provider, so Dapper and plain `DbCommand` code too. For EF Core, add [`ChaosDotNet.EntityFrameworkCore`](https://www.nuget.org/packages/ChaosDotNet.EntityFrameworkCore).

```shell
dotnet add package ChaosDotNet.Sql
```

```csharp
var orders = new SqlFactory()
    .After(5, TimeUnit.Seconds).For(20, TimeUnit.Seconds).CommandTimeout();

DbConnection connection = orders.CreateConnection(new SqlConnection(connectionString));
DbDataSource dataSource = orders.CreateDataSource(NpgsqlDataSource.Create(connectionString));
```

Connection opens, commands (`ExecuteReader`, `ExecuteNonQuery`, `ExecuteScalar`) and transaction commits follow the timeline.

## Filter calls

`When(...)` gets a `SqlChaosCall`:

```csharp
new SqlFactory()
    .When(call => call.CommandText?.Contains("INSERT") == true)
    .ForCalls(2).Fail(SqlServerFaults.Deadlock);
```

`call.Operation` is `Open`, `BeginTransaction`, `ExecuteReader`, `ExecuteNonQuery`, `ExecuteScalar`, `Commit` or `Rollback`. `call.IsCommand` is true for the three `Execute` operations.

## Faults

| Fault | Effect |
| --- | --- |
| `CommandTimeout()` | Throws a transient `DbException` for a timeout |
| `ConnectionFailure()` | Throws a transient `DbException` for a lost connection |
| `BreakReaderMidway(exception?)` | The query runs, then the reader throws after 0 to 4 rows |
| `Fail(SqlServerFaults.Deadlock)` | Real provider exceptions, from [`ChaosDotNet.SqlServer`](https://www.nuget.org/packages/ChaosDotNet.SqlServer) or [`ChaosDotNet.Npgsql`](https://www.nuget.org/packages/ChaosDotNet.Npgsql) |

Plus `Freeze()`, `Latency(...)`, `Jitter(...)`, `Fail(...)` and `FailRandomly(...)` from the core.

## Chaos monkey

The default catalogue is connection failures, command timeouts, readers that break midway, freeze, latency and jitter, with generic `DbException`s. Call `UseSqlServerFaults()` or `UseNpgsqlFaults()` to use real provider exceptions instead.

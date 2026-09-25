# ChaosDotNet.Npgsql

Real `PostgresException` and `NpgsqlException` instances for `SqlFactory` windows.

```shell
dotnet add package ChaosDotNet.Npgsql
```

```csharp
new SqlFactory().ForCalls(2).Fail(NpgsqlFaults.Deadlock);
```

| Fault | SQLSTATE | Transient |
| --- | --- | --- |
| `NpgsqlFaults.Deadlock` | 40P01 | Yes |
| `NpgsqlFaults.SerializationFailure` | 40001 | Yes |
| `NpgsqlFaults.AdminShutdown` | 57P01 | Yes |
| `NpgsqlFaults.TooManyConnections` | 53300 | Yes |
| `NpgsqlFaults.StatementTimeout` | 57014 | No |
| `NpgsqlFaults.UniqueViolation` | 23505 | No |
| `NpgsqlFaults.ConnectionLost` | (client) | Yes |
| `NpgsqlFaults.Timeout` | (client) | Yes |
| `NpgsqlFaults.Create(sqlState, message)` | Any | By SQLSTATE |

## Dependency injection

`services.AddChaosMonkey(monkey, chaos => chaos.Npgsql())` wraps the registered `DbDataSource` and `DbConnection`, named `postgres`, with Npgsql errors. The app's own pooler or data source still runs.

`chaos.Npgsql(ChaosStrategy.Replace, "Host=localhost;Database=shop;Username=app;Password=secret")` drops the app's registrations and uses a standard data source from the connection string instead. Only code that asks for `DbDataSource` or `DbConnection` gets chaos.

## Chaos monkey

```csharp
var orders = new SqlFactory(monkey).Named("orders").UseNpgsqlFaults();
```

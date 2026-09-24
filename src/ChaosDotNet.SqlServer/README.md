# ChaosDotNet.SqlServer

Real `SqlException` instances for `SqlFactory` windows. EF Core and SqlClient retry logic treat them exactly like the real errors.

```shell
dotnet add package ChaosDotNet.SqlServer
```

```csharp
new SqlFactory().ForCalls(2).Fail(SqlServerFaults.Deadlock);
```

| Fault | Error |
| --- | --- |
| `SqlServerFaults.Deadlock` | 1205 |
| `SqlServerFaults.Timeout` | -2 |
| `SqlServerFaults.DatabaseUnavailable` | 40613 |
| `SqlServerFaults.ServiceBusy` | 40501 |
| `SqlServerFaults.ConnectionReset` | 10054 |
| `SqlServerFaults.CannotOpenDatabase` | 4060 |
| `SqlServerFaults.UniqueConstraintViolation` | 2627 (not transient) |
| `SqlServerFaults.RandomTransient` | A random transient number each call |
| `SqlServerFaults.Create(number, message)` | Any error |

## Chaos monkey

```csharp
var orders = new SqlFactory(monkey).Named("orders").UseSqlServerFaults();
```

The monkey then uses deadlocks, timeouts, an unavailable database, reset connections, random transient errors and readers that break midway, all as real `SqlException`s.

Works with Microsoft.Data.SqlClient 5.2 and later. `SqlException` has no public constructor, so the package builds it through SqlClient's internal factory; CI checks this against the latest SqlClient.

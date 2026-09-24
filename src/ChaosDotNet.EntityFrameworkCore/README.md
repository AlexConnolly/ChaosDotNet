# ChaosDotNet.EntityFrameworkCore

Chaos for EF Core, through an interceptor made from a `SqlFactory`.

```shell
dotnet add package ChaosDotNet.EntityFrameworkCore
```

```csharp
var orders = new SqlFactory()
    .When(call => call.CommandText?.Contains("INSERT") == true)
    .ForCalls(2).Fail(SqlServerFaults.Deadlock);

services.AddDbContext<OrdersDb>(options => options
    .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
    .AddInterceptors(orders.CreateInterceptor()));

// ... save an order ...

orders.Verify().FaultsInjected(exactly: 2);
```

Connection opens, queries, `SaveChanges` commands and commits follow the timeline. Injected exceptions go through EF Core's normal paths, so retrying execution strategies retry them and `SaveChanges` wraps them in `DbUpdateException`.

Every `SqlFactory` fault works, including `BreakReaderMidway()`, which makes a query fail partway through reading its rows. See [`ChaosDotNet.Sql`](https://www.nuget.org/packages/ChaosDotNet.Sql) for filters and faults.

Tip: create the schema before the timeline starts, or filter it out with `When(...)`, so `EnsureCreated` is not affected.

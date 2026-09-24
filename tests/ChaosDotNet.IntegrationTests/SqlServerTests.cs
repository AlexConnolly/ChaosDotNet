using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace ChaosDotNet.IntegrationTests;

public sealed class SqlServerFixture : ContainerFixture<MsSqlContainer>
{
    protected override MsSqlContainer Build() => new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
}

public sealed class SqlServerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task EF_Core_retries_injected_deadlocks_and_saves()
    {
        fixture.SkipWithoutDocker();
        var factory = new SqlFactory(_clock)
            .When(call => call.CommandText?.Contains("INSERT", StringComparison.OrdinalIgnoreCase) == true)
            .ForCalls(2).Fail(SqlServerFaults.Deadlock);
        await using var db = CreateContext(factory);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        db.Orders.Add(new Order { Sku = "A-1" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await db.Orders.CountAsync(TestContext.Current.CancellationToken));
        factory.Verify().FaultsInjected(exactly: 2).RecoveredWithin(0, TimeUnit.Seconds);
    }

    [Fact]
    public async Task EF_Core_without_retries_surfaces_the_real_exception()
    {
        fixture.SkipWithoutDocker();
        var factory = new SqlFactory(_clock)
            .When(call => call.CommandText?.Contains("INSERT", StringComparison.OrdinalIgnoreCase) == true)
            .ForCalls(1).Fail(SqlServerFaults.Timeout);
        await using var db = CreateContext(factory, retry: false);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        db.Orders.Add(new Order { Sku = "A-1" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(-2, Assert.IsType<SqlException>(error.InnerException).Number);
    }

    [Fact]
    public async Task Ado_veneer_freezes_real_queries_until_the_window_ends()
    {
        fixture.SkipWithoutDocker();
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).For(5, TimeUnit.Seconds).Freeze();
        await using var connection = factory.CreateConnection(new SqlConnection(fixture.Container.GetConnectionString()));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@VERSION";

        var query = command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(query.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Contains("Microsoft SQL Server", (string)(await query)!);
    }

    [Fact]
    public async Task Open_fails_then_connects_to_the_real_server()
    {
        fixture.SkipWithoutDocker();
        var factory = new SqlFactory(_clock).When(call => call.Operation == "Open").ForCalls(1).Fail(SqlServerFaults.DatabaseUnavailable);
        await using var connection = factory.CreateConnection(new SqlConnection(fixture.Container.GetConnectionString()));

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.OpenAsync(TestContext.Current.CancellationToken));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(40613, error.Number);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    private OrdersDb CreateContext(SqlFactory factory, bool retry = true)
    {
        var connectionString = new SqlConnectionStringBuilder(fixture.Container.GetConnectionString())
        {
            InitialCatalog = $"orders_{Guid.NewGuid():N}",
        }.ConnectionString;

        return new OrdersDb(new DbContextOptionsBuilder<OrdersDb>()
            .UseSqlServer(connectionString, sql =>
            {
                if (retry)
                {
                    sql.EnableRetryOnFailure(5, TimeSpan.FromMilliseconds(50), null);
                }
            })
            .AddInterceptors(factory.CreateInterceptor())
            .Options);
    }

    private sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public required string Sku { get; set; }
    }
}

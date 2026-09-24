using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChaosDotNet.Tests;

public sealed class EntityFrameworkCoreTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public EntityFrameworkCoreTests()
    {
        _connection.Open();
        using var db = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Interceptor_fails_queries_inside_the_window()
    {
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).ForCalls(1).Fail(SqlServerFaults.Timeout);
        await using var db = CreateContext(factory);

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Orders.ToListAsync(TestContext.Current.CancellationToken));
        var orders = await db.Orders.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(orders);
        factory.Verify().FaultsInjected(exactly: 1).CallsSucceeded(atLeast: 1);
    }

    [Fact]
    public async Task Interceptor_fails_save_changes_inside_the_window()
    {
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).ForCalls(1).Fail(SqlServerFaults.Deadlock);
        await using var db = CreateContext(factory);

        db.Orders.Add(new Order { Sku = "A-1" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1205, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(error.InnerException).Number);
        Assert.Equal(1, await db.Orders.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Injected_faults_are_not_logged_as_real_failures()
    {
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).ForCalls(1).Fail(SqlServerFaults.Timeout);
        await using var db = CreateContext(factory);

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Orders.ToListAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(factory.Log, e => e.Kind == ChaosEventKind.CallFailed);
    }

    [Fact]
    public async Task Interceptor_freezes_queries()
    {
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).For(3, TimeUnit.Seconds).Freeze();
        await using var db = CreateContext(factory);

        var query = db.Orders.CountAsync(TestContext.Current.CancellationToken);
        Assert.False(query.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(0, await query);
    }

    private OrdersDb CreateContext(SqlFactory factory) =>
        new(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_connection).AddInterceptors(factory.CreateInterceptor()).Options);

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

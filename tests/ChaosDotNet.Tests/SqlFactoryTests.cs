using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ChaosDotNet.Tests;

public sealed class SqlFactoryTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly string _connectionString = $"Data Source=chaos-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public SqlFactoryTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _keepAlive.Execute("CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Sku TEXT NOT NULL)");
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task Veneer_passes_through_with_dapper()
    {
        await using var connection = new SqlFactory(_clock).CreateConnection(new SqliteConnection(_connectionString));

        await connection.ExecuteAsync("INSERT INTO Orders (Sku) VALUES (@sku)", new { sku = "A-1" });
        var skus = await connection.QueryAsync<string>("SELECT Sku FROM Orders");

        Assert.Equal(["A-1"], skus);
    }

    [Fact]
    public void Open_fails_inside_the_window()
    {
        using var connection = new SqlFactory(_clock)
            .When(call => call.Operation == "Open").ForCalls(1).ConnectionFailure()
            .CreateConnection(new SqliteConnection(_connectionString));

        var error = Assert.Throws<ChaosDbException>(connection.Open);
        connection.Open();

        Assert.True(error.IsTransient);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Commands_fail_with_the_provider_exception()
    {
        var factory = new SqlFactory(_clock)
            .When(call => call.IsCommand).For(10, TimeUnit.Seconds).Fail(SqlServerFaults.Deadlock);
        await using var connection = factory.CreateConnection(new SqliteConnection(_connectionString));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => connection.ExecuteAsync("SELECT 1"));
        _clock.Advance(TimeSpan.FromSeconds(10));
        var one = await connection.ExecuteScalarAsync<int>("SELECT 1");

        Assert.Equal(1205, error.Number);
        Assert.Equal(1, one);
        factory.Verify().FaultsInjected(exactly: 1).RecoveredWithin(0, TimeUnit.Seconds);
    }

    [Fact]
    public void Every_command_shape_goes_through_the_timeline()
    {
        var factory = new SqlFactory(_clock).When(call => call.IsCommand).Forever().CommandTimeout();
        using var connection = factory.CreateConnection(new SqliteConnection(_connectionString));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        Assert.Throws<ChaosDbException>(() => command.ExecuteNonQuery());
        Assert.Throws<ChaosDbException>(() => command.ExecuteScalar());
        Assert.Throws<ChaosDbException>(() => command.ExecuteReader());

        Assert.Equal(["ExecuteNonQuery", "ExecuteScalar", "ExecuteReader"], factory.Log.Where(e => e.Kind == ChaosEventKind.FaultInjected).Select(e => e.Operation));
    }

    [Fact]
    public void When_can_filter_on_command_text()
    {
        using var connection = new SqlFactory(_clock)
            .When(call => call.CommandText?.Contains("Orders", StringComparison.Ordinal) == true)
            .Forever().Fail(NpgsqlFaults.Deadlock)
            .CreateConnection(new SqliteConnection(_connectionString));
        connection.Open();

        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT 1"));
        var error = Assert.Throws<Npgsql.PostgresException>(() => connection.Query("SELECT * FROM Orders"));
        Assert.Equal("40P01", error.SqlState);
    }

    [Fact]
    public async Task Commit_fails_and_the_transaction_can_roll_back()
    {
        var factory = new SqlFactory(_clock).When(call => call.Operation == "Commit").ForCalls(1).Fail(SqlServerFaults.ConnectionReset);
        await using var connection = factory.CreateConnection(new SqliteConnection(_connectionString));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("INSERT INTO Orders (Sku) VALUES ('lost')", transaction: transaction);
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => transaction.CommitAsync(TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Orders"));
    }

    [Fact]
    public async Task Freeze_holds_a_command_until_the_window_ends()
    {
        await using var connection = new SqlFactory(_clock)
            .When(call => call.IsCommand).For(5, TimeUnit.Seconds).Freeze()
            .CreateConnection(new SqliteConnection(_connectionString));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var query = connection.ExecuteScalarAsync<int>("SELECT 7");
        Assert.False(query.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(7, await query);
    }

    [Fact]
    public async Task Data_source_veneer_creates_chaotic_connections()
    {
        var factory = new SqlFactory(_clock).When(call => call.Operation == "Open").ForCalls(1).ConnectionFailure();
        var dataSource = factory.CreateDataSource(new SqliteDataSource(_connectionString));

        await Assert.ThrowsAsync<ChaosDbException>(async () => await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken));
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT 1"));
    }

    [Fact]
    public void Commands_reject_connections_from_outside_the_veneer()
    {
        using var connection = new SqlFactory(_clock).CreateConnection(new SqliteConnection(_connectionString));
        using var command = connection.CreateCommand();

        Assert.Throws<ArgumentException>(() => command.Connection = new SqliteConnection(_connectionString));
    }

    private sealed class SqliteDataSource(string connectionString) : DbDataSource
    {
        public override string ConnectionString => connectionString;

        protected override DbConnection CreateDbConnection() => new SqliteConnection(connectionString);
    }
}

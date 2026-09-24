using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ChaosDotNet.IntegrationTests;

public sealed class PostgresFixture : ContainerFixture<PostgreSqlContainer>
{
    protected override PostgreSqlContainer Build() => new PostgreSqlBuilder("postgres:17-alpine").Build();
}

public sealed class RedisFixture : ContainerFixture<RedisContainer>
{
    protected override RedisContainer Build() => new RedisBuilder("redis:7-alpine").Build();
}

public sealed class PostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Data_source_veneer_fails_then_queries_the_real_server()
    {
        fixture.SkipWithoutDocker();
        var factory = new SqlFactory(new FakeTimeProvider())
            .When(call => call.IsCommand).ForCalls(1).Fail(NpgsqlFaults.SerializationFailure);
        await using var dataSource = factory.CreateDataSource(NpgsqlDataSource.Create(fixture.Container.GetConnectionString()));
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        var version = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        Assert.True(error.IsTransient);
        Assert.StartsWith("PostgreSQL 17", version, StringComparison.Ordinal);
    }
}

public sealed class RedisTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Veneer_times_out_matching_keys_then_reaches_the_real_server()
    {
        fixture.SkipWithoutDocker();
        var clock = new FakeTimeProvider();
        await using var real = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
        var factory = new RedisFactory(clock)
            .When(call => call.Key?.StartsWith("session:", StringComparison.Ordinal) == true)
            .For(30, TimeUnit.Seconds).Timeout();
        var database = factory.Create(real).GetDatabase();

        await database.StringSetAsync("cart:1", "apples");
        await Assert.ThrowsAsync<RedisTimeoutException>(() => database.StringSetAsync("session:1", "alex"));
        clock.Advance(TimeSpan.FromSeconds(30));
        await database.StringSetAsync("session:1", "alex");

        Assert.Equal("apples", (string?)await database.StringGetAsync("cart:1"));
        Assert.Equal("alex", (string?)await database.StringGetAsync("session:1"));
        factory.Verify().FaultsInjected(exactly: 1).RecoveredWithin(0, TimeUnit.Seconds);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChaosDotNet.Tests;

public sealed class WeirdHttpFaultTests
{
    private readonly FakeTimeProvider _clock = new();

    private HttpClient Client(Func<WindowBuilder<HttpFactory, HttpChaosCall>, HttpFactory> fault) =>
        fault(new HttpFactory(_clock, seed: 1).Forever()).CreateClient(new Uri("https://api.test"), new JsonServer());

    [Fact]
    public async Task Malformed_json_breaks_deserialisation()
    {
        using var client = Client(w => w.RespondMalformedJson());

        using var response = await client.GetAsync("/order", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<JsonException>(() => response.Content.ReadFromJsonAsync<Order>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Maintenance_page_is_a_200_with_html()
    {
        using var client = Client(w => w.RespondMaintenancePage());

        using var response = await client.GetAsync("/order", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadFromJsonAsync<Order>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Proxy_error_page_is_a_502_with_html()
    {
        using var client = Client(w => w.RespondProxyErrorPage());

        using var response = await client.GetAsync("/order", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("nginx", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_body_is_a_200_with_nothing_to_read()
    {
        using var client = Client(w => w.RespondEmpty());

        using var response = await client.GetAsync("/order", TestContext.Current.CancellationToken);

        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<JsonException>(() => response.Content.ReadFromJsonAsync<Order>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Garbage_body_is_random_bytes()
    {
        using var client = Client(w => w.RespondGarbage());

        using var first = await client.GetAsync("/order", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/order", TestContext.Current.CancellationToken);

        var a = await first.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var b = await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Unexpected_statuses_vary()
    {
        using var client = Client(w => w.RespondUnexpectedStatus());

        var statuses = new HashSet<HttpStatusCode>();
        for (var i = 0; i < 40; i++)
        {
            using var response = await client.GetAsync("/order", TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.True(statuses.Count >= 5);
        Assert.DoesNotContain(HttpStatusCode.OK, statuses);
    }

    [Fact]
    public async Task Body_breaks_midway_after_reaching_the_server()
    {
        var server = new JsonServer();
        using var client = new HttpFactory(_clock).Forever().BreakBodyMidway().CreateClient(new Uri("https://api.test"), server);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/order", TestContext.Current.CancellationToken));

        Assert.Equal(1, server.Requests);
        Assert.True(error is IOException || error.InnerException is IOException, error.ToString());
    }

    [Fact]
    public async Task Empty_bodies_have_nothing_to_break()
    {
        var server = new StubServer(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpFactory(_clock).Forever().BreakBodyMidway().CreateClient(new Uri("https://api.test"), server);

        using var response = await client.DeleteAsync("/order", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Random_network_errors_vary()
    {
        using var client = Client(w => w.RandomNetworkError());

        var errors = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/order", TestContext.Current.CancellationToken));
            errors.Add(error.Message);
        }

        Assert.True(errors.Count >= 4);
    }

    private sealed record Order(int Id, string Status);

    private sealed class StubServer(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class JsonServer : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Order(42, "paid")) });
        }
    }
}

public sealed class WeirdDataFaultTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly SqliteConnection _database = new("Data Source=:memory:");

    public WeirdDataFaultTests()
    {
        _database.Open();
        _database.Execute("CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Sku TEXT NOT NULL)");
        for (var i = 0; i < 10; i++)
        {
            _database.Execute("INSERT INTO Orders (Sku) VALUES (@sku)", new { sku = $"S-{i}" });
        }
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Ado_reader_breaks_after_a_few_rows()
    {
        using var connection = new SqlFactory(_clock, seed: 2).Forever().BreakReaderMidway(SqlServerFaults.ConnectionReset).CreateConnection(new NonClosingConnection(_database));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Sku FROM Orders";

        using var reader = command.ExecuteReader();
        var rows = 0;
        var error = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() =>
        {
            while (reader.Read())
            {
                rows++;
            }
        });

        Assert.InRange(rows, 0, 4);
        Assert.Equal(10054, error.Number);
    }

    [Fact]
    public void Reader_fault_does_not_touch_non_queries()
    {
        using var connection = new SqlFactory(_clock).Forever().BreakReaderMidway().CreateConnection(new NonClosingConnection(_database));

        Assert.Equal(10, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Orders"));
    }

    [Fact]
    public async Task EF_Core_reader_breaks_mid_query()
    {
        var factory = new SqlFactory(_clock).Forever().BreakReaderMidway();
        await using var db = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_database).AddInterceptors(factory.CreateInterceptor()).Options);

        await Assert.ThrowsAsync<ChaosDbException>(() => db.Orders.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Redis_server_errors_corruption_and_misses()
    {
        var multiplexer = FakeProxy.Create<IConnectionMultiplexer>((method, _) => FakeProxy.Create<IDatabase>((m, _) => m.Name switch
        {
            nameof(IDatabase.StringGetAsync) => Task.FromResult((RedisValue)"stored"),
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            _ => null,
        }));

        var server = new RedisFactory(_clock).Forever().ServerError().Create(multiplexer).GetDatabase();
        var corrupt = new RedisFactory(_clock).Forever().CorruptValues().Create(multiplexer).GetDatabase();
        var miss = new RedisFactory(_clock).Forever().Miss().Create(multiplexer).GetDatabase();

        var error = await Assert.ThrowsAsync<RedisServerException>(() => server.StringGetAsync("k"));
        var garbled = await corrupt.StringGetAsync("k");

        Assert.Matches("^(LOADING|READONLY|BUSY|MASTERDOWN|OOM|CLUSTERDOWN|TRYAGAIN) ", error.Message);
        Assert.NotEqual("stored", garbled.ToString());
        Assert.Equal(24, ((byte[])garbled!).Length);
        Assert.True((await miss.StringGetAsync("k")).IsNull);
        Assert.True(await corrupt.StringSetAsync("k", "v"));
    }

    [Fact]
    public async Task Cache_corruption_returns_random_bytes()
    {
        var inner = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = new CacheFactory(_clock).Forever().Corrupt().Create(inner);

        await cache.SetStringAsync("k", "value", TestContext.Current.CancellationToken);

        Assert.Equal(32, (await cache.GetAsync("k", TestContext.Current.CancellationToken))!.Length);
        Assert.Equal("value", await inner.GetStringAsync("k", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Service_bus_duplicates_sends_and_fills_queues()
    {
        var sender = new RecordingSender();
        var client = FakeServiceBus(sender);

        var duplicate = new ServiceBusFactory(_clock).ForCalls(1).Duplicate().CreateClient(client).CreateSender("orders");
        await duplicate.SendMessageAsync(new ServiceBusMessage("once"), TestContext.Current.CancellationToken);
        var full = new ServiceBusFactory(_clock).Forever().QuotaExceeded().CreateClient(client).CreateSender("orders");
        var error = await Assert.ThrowsAsync<ServiceBusException>(() => full.SendMessageAsync(new ServiceBusMessage("x"), TestContext.Current.CancellationToken));

        Assert.Equal(2, sender.Sent);
        Assert.Equal(ServiceBusFailureReason.QuotaExceeded, error.Reason);
    }

    private static ServiceBusClient FakeServiceBus(RecordingSender sender) => new SenderClient(sender);

    private sealed class SenderClient(RecordingSender sender) : ServiceBusClient
    {
        public override ServiceBusSender CreateSender(string queueOrTopicName) => sender;
    }

    private sealed class RecordingSender : ServiceBusSender
    {
        public int Sent { get; private set; }

        public override string EntityPath => "orders";

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }

    private sealed class NonClosingConnection(SqliteConnection inner) : System.Data.Common.DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;

        public override string DataSource => inner.DataSource;

        public override string ServerVersion => inner.ServerVersion;

        public override System.Data.ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);

        protected override System.Data.Common.DbCommand CreateDbCommand() => inner.CreateCommand();
    }

    private sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
    {
        public DbSet<OrderRow> Orders => Set<OrderRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<OrderRow>().ToTable("Orders");
    }

    private sealed class OrderRow
    {
        public int Id { get; set; }

        public required string Sku { get; set; }
    }
}

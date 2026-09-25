using System.Data.Common;
using System.Net;
using Azure.Messaging.ServiceBus;
using ChaosDotNet.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace ChaosDotNet.Tests;

public sealed class ChaosStrategyTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void Replace_removes_every_registration_and_keeps_the_lifetime()
    {
        var services = new ServiceCollection();
        services.AddScoped<IInventory, Inventory>();
        services.AddScoped<IInventory>(_ => new Inventory());

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Add(() => chaos.Replace<IInventory>(_ => new FixedInventory(7))));

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IInventory));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(7, scope.ServiceProvider.GetRequiredService<IInventory>().Count("a"));
    }

    [Fact]
    public async Task Http_replace_drops_the_apps_handlers_and_primary_handler()
    {
        var server = new CountingServer();
        var recorder = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.test"))
            .AddHttpMessageHandler(() => recorder)
            .ConfigurePrimaryHttpMessageHandler(() => server);

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Http(ChaosStrategy.Replace, (_, f) => f.Forever().Respond(HttpStatusCode.Accepted)));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        using var response = await client.GetAsync("/pay", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new Uri("https://payments.test"), client.BaseAddress);
        Assert.Equal(0, recorder.Calls);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Http_proxy_keeps_the_apps_handlers()
    {
        var server = new CountingServer();
        var recorder = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.test"))
            .AddHttpMessageHandler(() => recorder)
            .ConfigurePrimaryHttpMessageHandler(() => server);

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Http(ChaosStrategy.Proxy));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        using var response = await client.GetAsync("/pay", TestContext.Current.CancellationToken);

        Assert.Equal(1, recorder.Calls);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void Clock_proxy_bends_the_apps_own_clock()
    {
        var appClock = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(appClock);

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Clock(ChaosStrategy.Proxy, f => f.Forever().JumpForward(1, TimeUnit.Hours)));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(appClock.GetUtcNow().AddHours(1), provider.GetRequiredService<TimeProvider>().GetUtcNow());
    }

    [Fact]
    public void Clock_proxy_without_a_clock_bends_the_system_clock()
    {
        var services = new ServiceCollection();

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Clock(ChaosStrategy.Proxy, f => f.Forever().JumpForward(24, TimeUnit.Hours)));
        using var provider = services.BuildServiceProvider();

        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
        Assert.InRange(now - DateTimeOffset.UtcNow, TimeSpan.FromHours(23), TimeSpan.FromHours(25));
    }

    [Fact]
    public void Interface_replace_answers_from_the_subject_only()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInventory, Inventory>();

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Interface<IInventory>(ChaosStrategy.Replace, s => s.Setup(i => i.Count("a")).Returns(7)));
        using var provider = services.BuildServiceProvider();
        var inventory = provider.GetRequiredService<IInventory>();

        Assert.Equal(7, inventory.Count("a"));
        Assert.Equal(0, inventory.Count("b"));
    }

    [Fact]
    public void Interface_proxy_subject_uses_setups_and_passes_the_rest_to_the_app()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInventory, Inventory>();

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Interface<IInventory>(ChaosStrategy.Proxy, s => s.Setup(i => i.Count("a")).Returns(7)));
        using var provider = services.BuildServiceProvider();
        var inventory = provider.GetRequiredService<IInventory>();

        Assert.Equal(7, inventory.Count("a"));
        Assert.Equal(42, inventory.Count("b"));
    }

    [Fact]
    public async Task Cache_replace_needs_no_registered_cache()
    {
        var services = new ServiceCollection();

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.DistributedCache(ChaosStrategy.Replace));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetStringAsync("k", "v", TestContext.Current.CancellationToken);
        Assert.Equal("v", await cache.GetStringAsync("k", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Redis_and_service_bus_replace_need_connection_details()
    {
        var services = new ServiceCollection();
        var scenario = new ChaosScenario(_clock);

        Assert.Throws<ArgumentNullException>(() => services.AddChaos(scenario, chaos => chaos.Redis(ChaosStrategy.Replace)));
        Assert.Throws<ArgumentNullException>(() => services.AddChaos(scenario, chaos => chaos.ServiceBus(ChaosStrategy.Replace)));
        Assert.Throws<ArgumentNullException>(() => services.AddChaos(scenario, chaos => chaos.Npgsql(ChaosStrategy.Replace)));
    }

    [Fact]
    public void Redis_replace_drops_the_apps_multiplexer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_ => throw new InvalidOperationException("the app's own multiplexer"));

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Redis(ChaosStrategy.Replace, "localhost:1,abortConnect=false,connectTimeout=50"));

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = services.BuildServiceProvider();
        using var redis = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public async Task Service_bus_replace_drops_the_apps_client()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceBusClient>(_ => throw new InvalidOperationException("the app's own client"));

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.ServiceBus(ChaosStrategy.Replace, "Endpoint=sb://chaos.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA=="));
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<ServiceBusClient>();
        Assert.Equal("chaos.servicebus.windows.net", client.FullyQualifiedNamespace);
    }

    [Fact]
    public void Ef_core_replace_is_not_supported()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            new ServiceCollection().AddChaos(new ChaosScenario(_clock), chaos => chaos.EntityFrameworkCore(ChaosStrategy.Replace)));

        Assert.Contains("Npgsql(ChaosStrategy.Replace", error.Message);
    }

    [Fact]
    public async Task Sql_proxy_wraps_the_data_source_and_connection_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbDataSource>(_ => SqliteFactory.Instance.CreateDataSource("Data Source=:memory:"));
        services.AddTransient<DbConnection>(provider => provider.GetRequiredService<DbDataSource>().CreateConnection());
        Factories.SqlFactory? sql = null;

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Sql(configure: f => sql = f.ForCalls(1).ConnectionFailure()));
        await using var provider = services.BuildServiceProvider();

        await using (var first = provider.GetRequiredService<DbConnection>())
        {
            await Assert.ThrowsAnyAsync<DbException>(() => first.OpenAsync(TestContext.Current.CancellationToken));
        }

        await using var second = provider.GetRequiredService<DbConnection>();
        await second.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(sql!.Log, e => e.Kind == ChaosEventKind.FaultInjected);
        Assert.Single(sql.Log, e => e.Kind == ChaosEventKind.CallSucceeded);
    }

    [Fact]
    public async Task Sql_replace_uses_the_given_data_source_instead_of_the_apps()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbDataSource>(_ => throw new InvalidOperationException("the app's own pooler"));
        services.AddTransient<DbConnection>(_ => throw new InvalidOperationException("the app's own pooler"));

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Sql(ChaosStrategy.Replace, _ => SqliteFactory.Instance.CreateDataSource("Data Source=:memory:")));
        await using var provider = services.BuildServiceProvider();

        await using var connection = provider.GetRequiredService<DbConnection>();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Npgsql_replace_drops_the_apps_pooler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<NpgsqlDataSource>(_ => throw new InvalidOperationException("the app's own pooler"));
        services.AddSingleton<DbDataSource>(provider => provider.GetRequiredService<NpgsqlDataSource>());

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Npgsql(ChaosStrategy.Replace, "Host=localhost;Database=shop;Username=app;Password=secret"));
        using var provider = services.BuildServiceProvider();

        var dataSource = provider.GetRequiredService<DbDataSource>();
        Assert.Contains("Host=localhost", dataSource.ConnectionString, StringComparison.Ordinal);
        Assert.IsNotAssignableFrom<NpgsqlDataSource>(dataSource);
        Assert.Single(services, d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    [Fact]
    public async Task Npgsql_replace_disposes_cleanly_with_the_container()
    {
        var services = new ServiceCollection();

        services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Npgsql(ChaosStrategy.Replace, "Host=localhost;Database=shop;Username=app;Password=secret"));
        var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<DbDataSource>();
        var npgsql = provider.GetRequiredService<NpgsqlDataSource>();

        await provider.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => npgsql.CreateConnection().Open());
        Assert.NotNull(dataSource);
    }

    private sealed class FixedInventory(int count) : IInventory
    {
        public int Count(string sku) => count;

        public Task<int> CountAsync(string sku, CancellationToken cancellationToken = default) => Task.FromResult(count);

        public Task ReserveAsync(string sku, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<int> PeekAsync(string sku) => ValueTask.FromResult(count);

        public ValueTask ReleaseAsync(string sku) => ValueTask.CompletedTask;
    }

    private sealed class CountingServer : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return base.SendAsync(request, cancellationToken);
        }
    }
}

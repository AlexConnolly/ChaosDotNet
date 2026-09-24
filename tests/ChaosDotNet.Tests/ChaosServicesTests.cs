using System.Net;
using ChaosDotNet.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace ChaosDotNet.Tests;

public sealed class ChaosServicesTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task Http_clients_get_the_chaos_handler_innermost()
    {
        var server = new CountingServer();
        var services = new ServiceCollection();
        services.AddTransient<RetryOnceHandler>();
        services.AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.test"))
            .AddHttpMessageHandler<RetryOnceHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => server);
        var scenario = new ChaosScenario(_clock);

        services.AddChaos(scenario, chaos => chaos.Http((name, f) => f.ForCalls(1).Respond(HttpStatusCode.ServiceUnavailable)));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        using var response = await client.GetAsync("/pay", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void Interfaces_are_wrapped_and_keep_their_lifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInventory, Inventory>();
        var scenario = new ChaosScenario(_clock);

        services.AddChaos(scenario, chaos => chaos.Interface<IInventory>(f => f.ForCalls(1).Fail<ChaosTestException>()));
        using var provider = services.BuildServiceProvider();
        var inventory = provider.GetRequiredService<IInventory>();

        Assert.Throws<ChaosTestException>(() => inventory.Count("a"));
        Assert.Equal(42, inventory.Count("a"));
        Assert.Same(inventory, provider.GetRequiredService<IInventory>());
    }

    [Fact]
    public async Task Caches_and_db_contexts_are_wrapped()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddDbContext<ShopDb>(o => o.UseSqlite(connection));
        var scenario = new ChaosScenario(_clock);

        services.AddChaos(scenario, chaos => chaos
            .DistributedCache(f => f.Forever().Miss())
            .EntityFrameworkCore((_, f) => f.When(c => c.IsCommand && c.CommandText!.Contains("SELECT") && c.CommandText.Contains("\"Items\"")).ForCalls(1).CommandTimeout()));
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync("k", "v", TestContext.Current.CancellationToken);
        Assert.Null(await cache.GetStringAsync("k", TestContext.Current.CancellationToken));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ChaosDbException>(() => db.Items.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Items.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Clock_replaces_or_adds_the_time_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        var scenario = new ChaosScenario(_clock);

        services.AddChaos(scenario, chaos => chaos.Clock(f => f.Forever().JumpForward(1, TimeUnit.Hours)));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(_clock.GetUtcNow().AddHours(1), provider.GetRequiredService<TimeProvider>().GetUtcNow());
        Assert.Single(services, d => d.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void Except_leaves_named_dependencies_alone_and_wrapped_lists_the_rest()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.test"));
        services.AddHttpClient("health", c => c.BaseAddress = new Uri("https://health.test"));
        services.AddHttpClient<TypedClient>();
        ChaosServicesBuilder? seen = null;

        services.AddChaos(new ChaosScenario(_clock), chaos => (seen = chaos).Http().Except("http:health"));

        Assert.Equal(["http:TypedClient", "http:payments"], seen!.Wrapped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_to_wrap_throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Interface<IInventory>()));
        Assert.Throws<InvalidOperationException>(() => services.AddChaos(new ChaosScenario(_clock), chaos => chaos.Http()));
    }

    [Fact]
    public void The_monkey_plans_for_every_wrapped_dependency()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("payments", c => c.BaseAddress = new Uri("https://payments.test"));
        services.AddSingleton<IInventory, Inventory>();
        services.AddDistributedMemoryCache();
        var monkey = new ChaosMonkey(_clock, seed: 3, new ChaosMonkeyOptions { Intensity = ChaosIntensity.High });

        services.AddChaosMonkey(monkey, chaos => chaos.Http().Interface<IInventory>().DistributedCache().Clock());
        monkey.Start();

        var dependencies = monkey.Plan.Incidents.SelectMany(i => i.Faults.Select(f => f.Dependency)).ToHashSet();
        Assert.Equal(["IInventory", "cache", "clock", "http:payments"], dependencies.Order(StringComparer.Ordinal));
    }

    private sealed class TypedClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    private sealed class Item
    {
        public int Id { get; set; }
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

    private sealed class RetryOnceHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return response;
            }

            response.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

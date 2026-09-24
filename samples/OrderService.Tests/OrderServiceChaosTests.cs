using System.Net;
using System.Net.Http.Json;
using ChaosDotNet;
using ChaosDotNet.Factories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderService;

namespace OrderService.Tests;

public sealed class OrderServiceChaosTests : IDisposable
{
    private readonly SqliteConnection _database = new("Data Source=:memory:");
    private readonly PaymentsServer _payments = new();

    public OrderServiceChaosTests()
    {
        _database.Open();
        using var db = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_database).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Order_succeeds_when_payments_return_503_three_times()
    {
        var payments = new HttpFactory().ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable);
        using var app = CreateApp(payments, new SqlFactory());
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrder("A-1", 10m), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, _payments.Charges);
        payments.Verify().FaultsInjected(exactly: 3).CallsSucceeded(exactly: 1);
    }

    [Fact]
    public async Task Order_is_rejected_cleanly_when_payments_stay_down()
    {
        var payments = new HttpFactory().Forever().ConnectionRefused();
        using var app = CreateApp(payments, new SqlFactory());
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrder("A-1", 10m), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, _payments.Charges);
        payments.Verify().FaultsInjected(exactly: 4);
    }

    [Fact]
    public async Task Frozen_payments_hit_the_timeout_and_the_order_is_rejected()
    {
        var payments = new HttpFactory().For(1, TimeUnit.Minutes).Freeze();
        using var app = CreateApp(payments, new SqlFactory());
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrder("A-1", 10m), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        payments.Verify().FaultsInjected(exactly: 4).NoCallsFrozen();
    }

    [Fact]
    public async Task A_database_deadlock_after_payment_is_a_bug_this_test_finds()
    {
        var database = new SqlFactory()
            .When(call => call.CommandText?.Contains("INSERT", StringComparison.Ordinal) == true)
            .ForCalls(1).Fail(SqlServerFaults.Deadlock);
        using var app = CreateApp(new HttpFactory(), database);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrder("A-1", 10m), TestContext.Current.CancellationToken);

        // The customer was charged but no order was saved: the service has no retry on the database
        // and no compensation for the payment. Chaos testing makes the gap visible.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, _payments.Charges);
        database.Verify().FaultsInjected(exactly: 1);
    }

    private WebApplicationFactory<Program> CreateApp(HttpFactory payments, SqlFactory database) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddDbContext<OrdersDb>(options => options.UseSqlite(_database).AddInterceptors(database.CreateInterceptor()));
            services.AddHttpClient<PaymentsClient>()
                .AddHttpMessageHandler(() => payments.CreateHandler())
                .ConfigurePrimaryHttpMessageHandler(() => _payments);
        }));

    private sealed class PaymentsServer : HttpMessageHandler
    {
        private int _charges;

        public int Charges => _charges;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _charges);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

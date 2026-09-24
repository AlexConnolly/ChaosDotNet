using System.Net;
using System.Net.Http.Json;
using ChaosDotNet;
using ChaosDotNet.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OrderService.Tests;

public sealed class OrderServiceMonkeyTests
{
    [Fact]
    public async Task The_monkey_finds_orders_lost_after_payment_without_any_scripted_faults()
    {
        var error = await Assert.ThrowsAsync<ChaosExplorationException>(() => ChaosMonkey.ExploreAsync(
            runs: 5,
            async monkey =>
            {
                await using var shop = new Shop(monkey);
                using var client = shop.App.CreateClient();
                var statuses = new List<HttpStatusCode>();

                await monkey.RunAsync(async () =>
                {
                    for (var i = 0; i < 30; i++)
                    {
                        using var response = await client.PostAsJsonAsync("/orders", new CreateOrder($"SKU-{i}", 10m));
                        statuses.Add(response.StatusCode);
                        await Task.Delay(TimeSpan.FromSeconds(2), monkey.Clock);
                    }
                });

                // Invariants that must hold whatever breaks:
                Assert.Equal(shop.Payments.Charges, await shop.CountOrdersAsync()); // every charge has an order, every order was charged
                Assert.DoesNotContain(HttpStatusCode.InternalServerError, statuses);  // failures are handled, not crashes
            },
            new ChaosExploreOptions { MaxShrinkRuns = 10 }));

        TestContext.Current.TestOutputHelper?.WriteLine(error.Message);
        Assert.NotEmpty(error.Failures);
        Assert.All(error.Failures, f => Assert.NotEmpty(f.ShrunkPlan.Incidents));
    }

    private sealed class Shop : IAsyncDisposable
    {
        private readonly SqliteConnection _database = new("Data Source=:memory:");

        public Shop(ChaosMonkey monkey)
        {
            _database.Open();
            using (var db = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_database).Options))
            {
                db.Database.EnsureCreated();
            }

            App = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            {
                services.AddDbContext<OrdersDb>(options => options.UseSqlite(_database));
                services.AddHttpClient<PaymentsClient>().ConfigurePrimaryHttpMessageHandler(() => Payments);

                // One line puts the whole app under the monkey.
                services.AddChaosMonkey(monkey, chaos => chaos.Http().EntityFrameworkCore((_, orders) => orders.UseSqlServerFaults()));
            }));
        }

        public WebApplicationFactory<Program> App { get; }

        public PaymentsServer Payments { get; } = new();

        public async Task<int> CountOrdersAsync()
        {
            await using var db = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseSqlite(_database).Options);
            return await db.Orders.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

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

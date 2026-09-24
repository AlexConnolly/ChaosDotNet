using Microsoft.EntityFrameworkCore;
using Polly.Timeout;

namespace OrderService;

public sealed record CreateOrder(string Sku, decimal Amount);

public sealed class Order
{
    public int Id { get; set; }

    public required string Sku { get; set; }

    public decimal Amount { get; set; }
}

public sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public sealed class PaymentsClient(HttpClient http)
{
    public async Task<bool> TryChargeAsync(string sku, decimal amount, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/charges", new { sku, amount }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException)
        {
            return false;
        }
    }
}

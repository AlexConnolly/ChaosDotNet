using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using OrderService;
using Polly;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Orders") ?? "Data Source=orders.db"));

builder.Services
    .AddHttpClient<PaymentsClient>(client => client.BaseAddress = new Uri(builder.Configuration["Payments:BaseAddress"] ?? "https://payments.example"))
    .AddResilienceHandler("payments", pipeline =>
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(50),
            BackoffType = DelayBackoffType.Constant,
            UseJitter = false,
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(1));
    });

var app = builder.Build();

app.MapPost("/orders", async (CreateOrder request, OrdersDb db, PaymentsClient payments, CancellationToken cancellationToken) =>
{
    if (!await payments.TryChargeAsync(request.Sku, request.Amount, cancellationToken))
    {
        return Results.Problem("Payments are unavailable. Try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var order = new Order { Sku = request.Sku, Amount = request.Amount };
    db.Orders.Add(order);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/orders/{order.Id}", order);
});

app.MapGet("/orders/{id:int}", async (int id, OrdersDb db, CancellationToken cancellationToken) =>
    await db.Orders.FindAsync([id], cancellationToken) is { } order ? Results.Ok(order) : Results.NotFound());

app.Run();

public partial class Program;

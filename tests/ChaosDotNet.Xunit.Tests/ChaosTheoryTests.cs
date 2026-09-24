using System.Collections.Concurrent;

namespace ChaosDotNet.Xunit.Tests;

public interface IStock
{
    int Reserve(string sku);
}

public sealed class ChaosTheoryTests
{
    private static readonly ConcurrentDictionary<int, ChaosMonkey> Seen = new();
    private static readonly MonkeyFault Down = new("Down", MonkeyFaultKind.Outage, _ => new FailFault(_ => new InvalidOperationException("stock is down")));

    [ChaosTheory(Runs = 5)]
    public async Task Each_seed_gets_a_new_monkey(ChaosMonkey monkey)
    {
        Assert.InRange(monkey.Seed, 1, 5);
        Assert.True(Seen.TryAdd(monkey.Seed, monkey), $"Seed {monkey.Seed} ran twice.");
        Assert.Null(monkey.StartedAt);

        var stock = new ProxyFactory<IStock>(monkey).Named("stock").Create(new Stock());
        await monkey.RunAsync(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    stock.Reserve("a");
                }
                catch (Exception ex) when (ex is not ChaosFreezeTimeoutException)
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(3), monkey.Clock, TestContext.Current.CancellationToken);
            }
        });

        Assert.NotEmpty(monkey.Plan.Incidents);
    }

    [ChaosTheory(Seeds = [3, 17], Intensity = ChaosIntensity.High, DurationSeconds = 30)]
    public void Pinned_seeds_and_options_reach_the_monkey(ChaosMonkey monkey)
    {
        Assert.Contains(monkey.Seed, new[] { 3, 17 });
        Assert.Equal(ChaosIntensity.High, monkey.Options.Intensity);
        Assert.Equal(TimeSpan.FromSeconds(30), monkey.Options.Duration);
    }

    /// <summary>
    /// Fails on purpose, to show the shrunk plan in the failure message. Run it with
    /// <c>dotnet test --project tests/ChaosDotNet.Xunit.Tests -- --explicit only</c>.
    /// </summary>
    [ChaosTheory(Runs = 3, Explicit = true)]
    public async Task Demo_failure_shows_the_shrunk_plan(ChaosMonkey monkey)
    {
        var stock = new ProxyFactory<IStock>(monkey).Named("stock").WithMonkeyFaults(Down).Create(new Stock());
        var cache = new ProxyFactory<IStock>(monkey).Named("cache").WithMonkeyFaults(Down).Create(new Stock());

        await monkey.RunAsync(async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    cache.Reserve("a");
                }
                catch (InvalidOperationException)
                {
                }

                stock.Reserve("a");
                await Task.Delay(TimeSpan.FromSeconds(1), monkey.Clock, TestContext.Current.CancellationToken);
            }
        });
    }

    private sealed class Stock : IStock
    {
        public int Reserve(string sku) => 1;
    }
}

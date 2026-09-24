namespace ChaosDotNet.Tests;

public sealed class ScenarioAndVerifyTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void Scenario_starts_all_factories_together()
    {
        var scenario = new ChaosScenario(_clock, seed: 10);
        var first = new ProxyFactory<IInventory>(scenario).For(10, TimeUnit.Seconds).Fail<ChaosTestException>();
        var second = new ProxyFactory<IInventory>(scenario).For(10, TimeUnit.Seconds).Fail<ChaosTestException>();

        first.Create(new Inventory());
        _clock.Advance(TimeSpan.FromSeconds(10));
        var late = second.Create(new Inventory());

        Assert.Equal(42, late.Count("a"));
        Assert.Equal(first.Engine.StartedAt, second.Engine.StartedAt);
        Assert.NotEqual(first.Seed, second.Seed);
    }

    [Fact]
    public void Racing_first_calls_in_a_scenario_keep_every_window()
    {
        var method = typeof(IInventory).GetMethod(nameof(IInventory.Count))!;
        for (var i = 0; i < 300; i++)
        {
            var scenario = new ChaosScenario(_clock);
            var engines = Enumerable.Range(0, 64)
                .Select(_ => new ProxyFactory<IInventory>(scenario).For(1, TimeUnit.Hours).Fail<ChaosTestException>().Engine)
                .ToArray();
            var callers = new[] { engines[0], engines[^1], engines[^2], engines[^3] };
            using var barrier = new Barrier(callers.Length);

            var threads = callers.Select(engine => new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    engine.BeforeCall(new Proxies.ProxyChaosCall(method, ["a"]));
                }
                catch (ChaosTestException)
                {
                }
            })).ToArray();
            Array.ForEach(threads, t => t.Start());
            Array.ForEach(threads, t => t.Join());

            Assert.All(callers, engine => Assert.Contains(engine.Log, e => e.Kind == ChaosEventKind.FaultInjected));
        }
    }

    [Fact]
    public void Scenario_start_starts_every_factory()
    {
        var scenario = new ChaosScenario(_clock);
        var factory = new ProxyFactory<IInventory>(scenario);

        scenario.Start();

        Assert.Equal(_clock.GetUtcNow(), factory.Engine.StartedAt);
    }

    [Fact]
    public void FaultsInjected_counts_faults()
    {
        var factory = new ProxyFactory<IInventory>(_clock).ForCalls(2).Fail<ChaosTestException>();
        var veneer = factory.Create(new Inventory());
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        veneer.Count("a");

        factory.Verify().FaultsInjected(exactly: 2).FaultsInjected(window: 0).CallsSucceeded(exactly: 1);
        var error = Assert.Throws<ChaosAssertionException>(() => factory.Verify().FaultsInjected(atLeast: 3));

        Assert.Contains("Expected at least 3 faults injected in all windows, but there were 2.", error.Message);
        Assert.Contains("FaultInjected", error.Message);
    }

    [Fact]
    public async Task RecoveredWithin_measures_from_the_window_end_to_the_first_success()
    {
        var factory = new ProxyFactory<IInventory>(_clock).For(10, TimeUnit.Seconds).Fail<ChaosTestException>();
        var veneer = factory.Create(new Inventory());
        await Assert.ThrowsAsync<ChaosTestException>(() => veneer.CountAsync("a", TestContext.Current.CancellationToken));

        _clock.Advance(TimeSpan.FromSeconds(13));
        await veneer.CountAsync("a", TestContext.Current.CancellationToken);

        factory.Verify().RecoveredWithin(3, TimeUnit.Seconds);
        var error = Assert.Throws<ChaosAssertionException>(() => factory.Verify().RecoveredWithin(2, TimeUnit.Seconds));
        Assert.Contains("Recovery took 00:00:03", error.Message);
    }

    [Fact]
    public void RecoveredWithin_fails_while_the_window_is_active()
    {
        var factory = new ProxyFactory<IInventory>(_clock).For(10, TimeUnit.Seconds).Fail<ChaosTestException>();
        var veneer = factory.Create(new Inventory());
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));

        var error = Assert.Throws<ChaosAssertionException>(() => factory.Verify().RecoveredWithin(1, TimeUnit.Minutes));

        Assert.Contains("has not ended", error.Message);
    }

    [Fact]
    public void RecoveredWithin_fails_without_a_success()
    {
        var factory = new ProxyFactory<IInventory>(_clock).ForCalls(1).Fail<ChaosTestException>();
        var veneer = factory.Create(new Inventory());
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        Assert.Throws<ChaosAssertionException>(() => factory.Verify().RecoveredWithin(1, TimeUnit.Minutes));
    }

    [Fact]
    public void NoCallsFrozen_fails_while_a_call_is_frozen()
    {
        var factory = new ProxyFactory<IInventory>(_clock).For(10, TimeUnit.Seconds).Freeze();
        var veneer = factory.Create(new Inventory());

        _ = veneer.CountAsync("a", TestContext.Current.CancellationToken);

        Assert.Throws<ChaosAssertionException>(() => factory.Verify().NoCallsFrozen());
        _clock.Advance(TimeSpan.FromSeconds(10));
        factory.Verify().NoCallsFrozen();
    }
}

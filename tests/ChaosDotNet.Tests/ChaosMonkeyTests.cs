using System.Diagnostics;

namespace ChaosDotNet.Tests;

public sealed class ChaosMonkeyTests
{
    private static readonly MonkeyFault Down = new("Down", MonkeyFaultKind.Outage, _ => new FailFault(_ => new ChaosTestException()));

    [Fact]
    public void Same_seed_gives_the_same_plan_and_different_seeds_differ()
    {
        static string Plan(int seed)
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), seed);
            new HttpFactory(monkey).Named("payments");
            new ProxyFactory<IInventory>(monkey).Named("inventory");
            new ProxyFactory<IInventory>(monkey).Named("warehouse");
            monkey.Start();
            return monkey.Plan.ToString();
        }

        Assert.Equal(Plan(7), Plan(7));
        Assert.NotEqual(Plan(7), Plan(8));
    }

    [Theory]
    [InlineData(ChaosIntensity.Low)]
    [InlineData(ChaosIntensity.Medium)]
    [InlineData(ChaosIntensity.High)]
    public void Plans_stay_inside_the_duration_and_the_concurrency_limit(ChaosIntensity intensity)
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), seed, new ChaosMonkeyOptions { Intensity = intensity, MaxConcurrentIncidents = 2 });
            foreach (var name in new[] { "a", "b", "c", "d" })
            {
                new ProxyFactory<IInventory>(monkey).Named(name);
            }

            monkey.Start();
            var incidents = monkey.Plan.Incidents;

            Assert.NotEmpty(incidents);
            Assert.All(incidents, i => Assert.InRange(i.End, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
            foreach (var incident in incidents)
            {
                var overlapping = incidents.Where(o => o.Start < incident.End && o.End > incident.Start).ToList();
                Assert.InRange(overlapping.Count, 1, 2);
                var dependencies = overlapping.SelectMany(o => o.Faults.Select(f => f.Dependency)).ToList();
                Assert.Equal(dependencies.Count, dependencies.Distinct().Count());
            }
        }
    }

    [Fact]
    public void Higher_intensity_causes_more_incidents()
    {
        static int Count(ChaosIntensity intensity) => Enumerable.Range(1, 20).Sum(seed =>
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), seed, new ChaosMonkeyOptions { Intensity = intensity });
            new ProxyFactory<IInventory>(monkey).Named("a");
            new ProxyFactory<IInventory>(monkey).Named("b");
            monkey.Start();
            return monkey.Plan.Incidents.Count;
        });

        Assert.True(Count(ChaosIntensity.Low) < Count(ChaosIntensity.Medium));
        Assert.True(Count(ChaosIntensity.Medium) < Count(ChaosIntensity.High));
    }

    [Fact]
    public void Correlated_incidents_break_every_dependency_in_them_at_the_same_time()
    {
        var (clock, monkey, first, second, incident) = Enumerable.Range(1, 200).Select(seed =>
        {
            var c = new FakeTimeProvider();
            var m = new ChaosMonkey(c, seed);
            var a = new ProxyFactory<IInventory>(m).Named("a").WithMonkeyFaults(Down);
            var b = new ProxyFactory<IInventory>(m).Named("b").WithMonkeyFaults(Down);
            m.Start();
            var shared = m.Plan.Incidents.FirstOrDefault(i => i.Faults.Count == 2 && i.Rate == 1);
            return (c, m, a, b, shared);
        }).First(x => x.shared is not null);

        var a = first.Create(new Inventory());
        var b = second.Create(new Inventory());

        clock.Advance(incident!.Start);
        Assert.Throws<ChaosTestException>(() => a.Count("x"));
        Assert.Throws<ChaosTestException>(() => b.Count("x"));

        clock.Advance(incident.Length);
        var nextStart = monkey.Plan.Incidents.Where(i => i.Start >= incident.End).Select(i => i.Start).DefaultIfEmpty(TimeSpan.MaxValue).Min();
        if (nextStart > incident.End)
        {
            Assert.Equal(42, a.Count("x"));
            Assert.Equal(42, b.Count("x"));
        }
    }

    [Fact]
    public void Incidents_break_and_then_recover_a_dependency()
    {
        var (clock, monkey, factory, incident) = Enumerable.Range(1, 50).Select(seed =>
        {
            var c = new FakeTimeProvider();
            var m = new ChaosMonkey(c, seed);
            var f = new ProxyFactory<IInventory>(m).Named("inventory").WithMonkeyFaults(Down);
            m.Start();
            return (c, m, f, m.Plan.Incidents.FirstOrDefault(i => i.Rate == 1));
        }).First(x => x.Item4 is not null);
        var veneer = factory.Create(new Inventory());

        clock.Advance(incident!.Start);
        Assert.Throws<ChaosTestException>(() => veneer.Count("x"));
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(42, veneer.Count("x"));
        Assert.Equal("inventory", monkey.Plan.Incidents[0].Faults[0].Dependency);
    }

    [Fact]
    public void Hand_written_windows_are_left_alone()
    {
        var monkey = new ChaosMonkey(new FakeTimeProvider(), seed: 1);
        new ProxyFactory<IInventory>(monkey).Named("scripted").For(1, TimeUnit.Hours).Freeze();
        new ProxyFactory<IInventory>(monkey).Named("random");

        monkey.Start();

        Assert.DoesNotContain(monkey.Plan.Incidents, i => i.Faults.Any(f => f.Dependency == "scripted"));
        Assert.Contains(monkey.Plan.Incidents, i => i.Faults.Any(f => f.Dependency == "random"));
    }

    [Fact]
    public void Data_loss_faults_are_only_used_when_allowed()
    {
        var dataLoss = new MonkeyFault("Lose", MonkeyFaultKind.DataLoss, _ => new FailFault(_ => new ChaosTestException()));

        static ChaosMonkey Run(MonkeyFault fault, bool allow)
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), 1, new ChaosMonkeyOptions { AllowDataLoss = allow });
            new ProxyFactory<IInventory>(monkey).Named("a").WithMonkeyFaults(fault);
            monkey.Start();
            return monkey;
        }

        Assert.Empty(Run(dataLoss, allow: false).Plan.Incidents);
        Assert.NotEmpty(Run(dataLoss, allow: true).Plan.Incidents);
    }

    [Fact]
    public void Factories_get_default_names_and_duplicates_are_rejected()
    {
        var monkey = new ChaosMonkey(new FakeTimeProvider(), 1);
        var http = new HttpFactory(monkey);
        var proxy = new ProxyFactory<IInventory>(monkey);
        new ProxyFactory<IInventory>(monkey).Named("same");
        new ProxyFactory<IInventory>(monkey).Named("same");

        Assert.Equal("http1", http.Name);
        Assert.Equal("proxy2", proxy.Name);
        Assert.Throws<InvalidOperationException>(monkey.Start);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_plays_a_whole_plan_on_a_fake_clock_in_a_fraction_of_a_second(bool paceOnMonkeyClock)
    {
        var fake = new FakeTimeProvider();
        var monkey = new ChaosMonkey(fake, 5, new ChaosMonkeyOptions { Intensity = ChaosIntensity.High });
        TimeProvider pace = paceOnMonkeyClock ? monkey.Clock : fake;
        var factory = new ProxyFactory<IInventory>(monkey).Named("inventory");
        var veneer = factory.Create(new Inventory());
        var start = pace.GetUtcNow();
        var timer = Stopwatch.StartNew();

        var calls = await monkey.RunAsync(async () =>
        {
            var count = 0;
            while (pace.GetUtcNow() - start < TimeSpan.FromSeconds(61))
            {
                try
                {
                    await veneer.CountAsync("x");
                }
                catch (Exception ex) when (ex is not ChaosFreezeTimeoutException)
                {
                }

                count++;
                await Task.Delay(TimeSpan.FromSeconds(1), pace);
            }

            return count;
        });

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Took {timer.Elapsed}");
        Assert.InRange(calls, 10, 61);
        Assert.Contains(factory.Log, e => e.Kind == ChaosEventKind.FaultInjected);
    }

    [Fact]
    public async Task RunAsync_gives_the_same_history_for_the_same_seed_even_when_the_workload_is_slow()
    {
        static async Task<string> History()
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), 21, new ChaosMonkeyOptions { Intensity = ChaosIntensity.High });
            var veneer = new ProxyFactory<IInventory>(monkey).Named("inventory").WithMonkeyFaults(Down).Create(new Inventory());
            var start = monkey.Clock.GetUtcNow();
            var history = new List<string>();

            await monkey.RunAsync(async () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    await Task.Run(() => Thread.Sleep(3));
                    var at = (monkey.Clock.GetUtcNow() - start).TotalSeconds;
                    try
                    {
                        veneer.Count("x");
                        history.Add($"{at}:ok");
                    }
                    catch (ChaosTestException)
                    {
                        history.Add($"{at}:fail");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1.5), monkey.Clock);
                }
            });

            return string.Join(' ', history);
        }

        var first = await History();

        Assert.Contains("fail", first);
        Assert.Equal(first, await History());
        Assert.Equal(first, await History());
    }

    [Fact]
    public async Task RunAsync_times_out_on_the_system_clock_too()
    {
        var monkey = new ChaosMonkey(TimeProvider.System, 1, new ChaosMonkeyOptions { RunTimeout = TimeSpan.FromMilliseconds(200) });

        var run = monkey.RunAsync(() => Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains("did not finish", error.Message);
    }

    [Fact]
    public async Task Explore_passes_when_every_seed_passes()
    {
        await ChaosMonkey.ExploreAsync(10, async monkey =>
        {
            var veneer = new ProxyFactory<IInventory>(monkey).Named("inventory").Create(new Inventory());
            await monkey.RunAsync(() => Task.CompletedTask);
            Assert.NotNull(veneer);
        });
    }

    [Fact]
    public async Task Explore_reports_failing_seeds_and_shrinks_to_the_incident_that_matters()
    {
        var error = await Assert.ThrowsAsync<ChaosExplorationException>(() => ChaosMonkey.ExploreAsync(20, Scenario));

        Assert.NotEmpty(error.Failures);
        Assert.All(error.Failures, failure =>
        {
            var incident = Assert.Single(failure.ShrunkPlan.Incidents);
            Assert.Contains(incident.Faults, f => f.Dependency == "orders");
            Assert.True(failure.Plan.Incidents.Count >= failure.ShrunkPlan.Incidents.Count);
        });
        Assert.Contains("Reproduce: set CHAOS_SEED=", error.Message);
        Assert.Contains("CHAOS_INCIDENTS=", error.Message);
        Assert.Contains("orders", error.Message);

        static async Task Scenario(ChaosMonkey monkey)
        {
            var clock = monkey.Clock;
            var orders = new ProxyFactory<IInventory>(monkey).Named("orders").WithMonkeyFaults(Down).Create(new Inventory());
            var cache = new ProxyFactory<IInventory>(monkey).Named("cache").WithMonkeyFaults(Down).Create(new Inventory());
            var start = clock.GetUtcNow();

            await monkey.RunAsync(async () =>
            {
                while (clock.GetUtcNow() - start < TimeSpan.FromSeconds(60))
                {
                    try
                    {
                        cache.Count("x");
                    }
                    catch (ChaosTestException)
                    {
                    }

                    orders.Count("x");
                    await Task.Delay(TimeSpan.FromMilliseconds(500), clock);
                }
            });
        }
    }

    [Fact]
    public async Task Explore_says_when_a_test_fails_without_any_chaos()
    {
        var error = await Assert.ThrowsAsync<ChaosExplorationException>(() => ChaosMonkey.ExploreAsync(1, monkey =>
        {
            new ProxyFactory<IInventory>(monkey).Named("inventory").Create(new Inventory());
            throw new InvalidOperationException("bug");
        }));

        Assert.Empty(Assert.Single(error.Failures).ShrunkPlan.Incidents);
        Assert.Contains("the test fails without chaos", error.Message);
    }

    [Fact]
    public async Task Explore_can_use_explicit_seeds()
    {
        var seen = new List<int>();

        await ChaosMonkey.ExploreAsync(1, monkey =>
        {
            seen.Add(monkey.Seed);
            return Task.CompletedTask;
        }, new ChaosExploreOptions { Seeds = [11, 22, 33] });

        Assert.Equal([11, 22, 33], seen);
    }

    [Fact]
    public void Every_catalogue_entry_builds_a_fault()
    {
        var random = new Random(1);
        IEnumerable<MonkeyFault>[] catalogues =
        [
            new HttpFactory().MonkeyFaults,
            new ProxyFactory<IInventory>().MonkeyFaults,
            new SqlFactory().MonkeyFaults,
            new SqlFactory().UseSqlServerFaults().MonkeyFaults,
            new SqlFactory().UseNpgsqlFaults().MonkeyFaults,
            new RedisFactory().MonkeyFaults,
            new CacheFactory().MonkeyFaults,
            new ServiceBusFactory().MonkeyFaults,
            new PollyFactory().MonkeyFaults,
        ];

        Assert.All(catalogues, catalogue =>
        {
            var entries = catalogue.ToList();
            Assert.True(entries.Count >= 4);
            Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct().Count());
            Assert.All(entries, e => Assert.NotNull(e.Create(random)));
        });
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection(typeof(EnvironmentCollection))]
public sealed class ChaosMonkeyEnvironmentTests
{
    [Fact]
    public void CHAOS_SEED_and_CHAOS_INCIDENTS_replay_a_plan()
    {
        var full = Build(seed: 99);
        var keep = full.Plan.Incidents.Take(2).Select(i => i.Number).ToList();

        Environment.SetEnvironmentVariable("CHAOS_SEED", "99");
        Environment.SetEnvironmentVariable("CHAOS_INCIDENTS", string.Join(',', keep));
        try
        {
            var replay = Build(seed: null);

            Assert.Equal(99, replay.Seed);
            Assert.Equal(keep, replay.Plan.Incidents.Select(i => i.Number));
            Assert.Equal(full.Plan.Incidents.Take(2).Select(i => i.ToString()), replay.Plan.Incidents.Select(i => i.ToString()));
            Assert.Equal($"CHAOS_SEED=99 CHAOS_INCIDENTS={string.Join(',', keep)}", replay.Plan.ReproduceWith);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHAOS_SEED", null);
            Environment.SetEnvironmentVariable("CHAOS_INCIDENTS", null);
        }

        static ChaosMonkey Build(int? seed)
        {
            var monkey = new ChaosMonkey(new FakeTimeProvider(), seed);
            new HttpFactory(monkey).Named("payments");
            new ProxyFactory<IInventory>(monkey).Named("inventory");
            monkey.Start();
            return monkey;
        }
    }
}

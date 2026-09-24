namespace ChaosDotNet.Tests;

public sealed class TimelineTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly Inventory _inner = new();

    private ProxyFactory<IInventory> Factory(int seed = 1) => new(_clock, seed);

    [Fact]
    public void Calls_pass_through_when_there_are_no_windows()
    {
        var veneer = Factory().Create(_inner);

        Assert.Equal(42, veneer.Count("a"));
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public void Timed_window_fails_calls_until_it_ends()
    {
        var veneer = Factory().For(10, TimeUnit.Seconds).Fail<ChaosTestException>().Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(9.999));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(42, veneer.Count("a"));
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public void After_delays_the_next_window()
    {
        var veneer = Factory().After(5, TimeUnit.Seconds).For(5, TimeUnit.Seconds).Fail<ChaosTestException>().Create(_inner);

        Assert.Equal(42, veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void Windows_chain_in_order()
    {
        var veneer = Factory()
            .For(2, TimeUnit.Seconds).Fail(() => new InvalidOperationException())
            .Then().For(2, TimeUnit.Seconds).Fail(() => new TimeoutException())
            .Create(_inner);

        Assert.Throws<InvalidOperationException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<TimeoutException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void A_large_clock_jump_skips_whole_windows()
    {
        var veneer = Factory()
            .For(1, TimeUnit.Seconds).Fail<ChaosTestException>()
            .Then().For(1, TimeUnit.Seconds).Fail<ChaosTestException>()
            .Create(_inner);

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void ForCalls_window_ends_after_that_many_calls()
    {
        var veneer = Factory().ForCalls(3).Fail<ChaosTestException>().Create(_inner);

        for (var i = 0; i < 3; i++)
        {
            Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        }

        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void Until_window_ends_when_the_condition_is_true()
    {
        var healthy = false;
        var veneer = Factory().Until(() => healthy).Fail<ChaosTestException>().Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        healthy = true;
        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void Forever_window_never_ends()
    {
        var veneer = Factory().Forever().Fail<ChaosTestException>().Create(_inner);

        _clock.Advance(TimeSpan.FromDays(365));

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
    }

    [Fact]
    public void Every_repeats_a_window_each_period()
    {
        var veneer = Factory().Every(30, TimeUnit.Seconds).For(5, TimeUnit.Seconds).Fail<ChaosTestException>().Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(42, veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void When_limits_a_window_to_matching_calls_and_only_they_count()
    {
        var veneer = Factory()
            .When(call => call.Arguments[0] as string == "hot")
            .ForCalls(1).Fail<ChaosTestException>()
            .Create(_inner);

        Assert.Equal(42, veneer.Count("cold"));
        Assert.Equal(42, veneer.Count("cold"));
        Assert.Throws<ChaosTestException>(() => veneer.Count("hot"));
        Assert.Equal(42, veneer.Count("hot"));
    }

    [Fact]
    public void Flaky_with_the_same_seed_gives_the_same_faults()
    {
        static bool[] Run(int seed)
        {
            var veneer = new ProxyFactory<IInventory>(new FakeTimeProvider(), seed)
                .Forever().Fail<ChaosTestException>().Flaky(0.3)
                .Create(new Inventory());

            return Enumerable.Range(0, 200).Select(_ =>
            {
                try
                {
                    veneer.Count("a");
                    return false;
                }
                catch (ChaosTestException)
                {
                    return true;
                }
            }).ToArray();
        }

        var first = Run(7);

        Assert.Equal(first, Run(7));
        Assert.NotEqual(first, Run(8));
        Assert.InRange(first.Count(f => f), 40, 80);
    }

    [Fact]
    public void Flaky_zero_never_injects_and_logs_skips()
    {
        var factory = Factory().ForCalls(2).Fail<ChaosTestException>().Flaky(0);
        var veneer = factory.Create(_inner);

        veneer.Count("a");
        veneer.Count("a");

        Assert.Equal(2, factory.Log.Count(e => e.Kind == ChaosEventKind.FaultSkipped));
        Assert.DoesNotContain(factory.Log, e => e.Kind == ChaosEventKind.FaultInjected);
    }

    [Fact]
    public void Timeline_starts_on_first_create_not_on_construction()
    {
        var factory = Factory().For(10, TimeUnit.Seconds).Fail<ChaosTestException>();
        _clock.Advance(TimeSpan.FromMinutes(5));

        var veneer = factory.Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
    }

    [Fact]
    public void Start_fixes_the_start_time()
    {
        var factory = Factory().For(10, TimeUnit.Seconds).Fail<ChaosTestException>().Start();
        _clock.Advance(TimeSpan.FromSeconds(10));

        var veneer = factory.Create(_inner);

        Assert.Equal(42, veneer.Count("a"));
    }

    [Fact]
    public void Log_records_windows_faults_and_calls()
    {
        var factory = Factory().For(1, TimeUnit.Seconds).Fail<ChaosTestException>();
        var veneer = factory.Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        veneer.Count("a");

        Assert.Equal(
            [ChaosEventKind.TimelineStarted, ChaosEventKind.WindowStarted, ChaosEventKind.FaultInjected, ChaosEventKind.WindowEnded, ChaosEventKind.CallSucceeded],
            factory.Log.Select(e => e.Kind));
        Assert.Equal("Count", factory.Log[2].Operation);
    }

    [Fact]
    public void Real_client_failures_are_logged()
    {
        var factory = Factory();
        var veneer = factory.Create(_inner);
        _inner.Throw = new InvalidOperationException("real");

        Assert.Throws<InvalidOperationException>(() => veneer.Count("a"));

        var failed = Assert.Single(factory.Log, e => e.Kind == ChaosEventKind.CallFailed);
        Assert.Same(_inner.Throw, failed.Exception);
    }

    [Fact]
    public void Nothing_can_follow_a_window_that_never_ends()
    {
        var factory = Factory().Forever().Freeze();

        Assert.Throws<InvalidOperationException>(() => factory.For(1, TimeUnit.Seconds).Freeze());
    }

    [Fact]
    public void Timeline_cannot_change_after_it_starts()
    {
        var factory = Factory();
        factory.Create(_inner);

        Assert.Throws<InvalidOperationException>(() => factory.For(1, TimeUnit.Seconds).Freeze());
    }

    [Fact]
    public void A_window_takes_only_one_fault()
    {
        var window = Factory().For(1, TimeUnit.Seconds);
        window.Freeze();

        Assert.Throws<InvalidOperationException>(() => window.Freeze());
    }

    [Fact]
    public void Flaky_needs_a_window()
    {
        Assert.Throws<InvalidOperationException>(() => Factory().Flaky(0.5));
    }

    [Fact]
    public void When_must_be_followed_by_a_window()
    {
        Assert.Throws<InvalidOperationException>(() => Factory().When(_ => true).After(1, TimeUnit.Seconds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Negative_or_invalid_amounts_are_rejected(double amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Factory().For(amount, TimeUnit.Seconds));
    }

    [Fact]
    public void Every_window_must_be_shorter_than_its_period()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Factory().Every(5, TimeUnit.Seconds).For(5, TimeUnit.Seconds));
    }

    [Theory]
    [InlineData(1500, TimeUnit.Milliseconds, 1.5)]
    [InlineData(2, TimeUnit.Minutes, 120)]
    [InlineData(1, TimeUnit.Hours, 3600)]
    public void Time_units_convert(double amount, TimeUnit unit, double seconds)
    {
        var veneer = Factory().For(amount, unit).Fail<ChaosTestException>().Create(_inner);

        _clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(42, veneer.Count("a"));
    }
}

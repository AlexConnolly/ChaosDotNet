namespace ChaosDotNet.Tests;

public interface IPaymentGateway
{
    string Name { get; }

    Task<Receipt> ChargeAsync(string sku, decimal amount);

    ValueTask<int> CountAsync(string sku);

    Task RefundAsync(string sku);

    ValueTask PingAsync();

    decimal Price(string sku);

    void Log(string message);

    IReadOnlyList<string> Skus();

    T Get<T>(string key);

    string Format(string template, params object[] args);

    int Total(IReadOnlyList<int> values);
}

public sealed record Receipt(string Sku, decimal Amount);

public sealed class ChaosSubjectTests
{
    private readonly FakeTimeProvider _clock = new();

    private ChaosSubject<IPaymentGateway> Subject(bool strict = false) => new(_clock, seed: 1, strict: strict);

    [Fact]
    public async Task Setups_work_for_every_return_shape()
    {
        var logged = new List<string>();
        var gateway = Subject()
            .Setup(g => g.Name).Returns("stripe")
            .Setup(g => g.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>())).Returns((string sku, decimal amount) => new Receipt(sku, amount))
            .Setup(g => g.CountAsync("a")).Returns(3)
            .Setup(g => g.Price("a")).Returns(9.99m)
            .Setup(g => g.Log(Arg.Any<string>())).Callback<string>(logged.Add)
            .Setup(g => g.Skus()).Returns(["a", "b"])
            .Setup(g => g.Get<int>("n")).Returns(7)
            .Create();

        Assert.Equal("stripe", gateway.Name);
        Assert.Equal(new Receipt("x", 5m), await gateway.ChargeAsync("x", 5m));
        Assert.Equal(3, await gateway.CountAsync("a"));
        Assert.Equal(9.99m, gateway.Price("a"));
        gateway.Log("hello");
        Assert.Equal(["a", "b"], gateway.Skus());
        Assert.Equal(7, gateway.Get<int>("n"));
        await gateway.RefundAsync("a");
        await gateway.PingAsync();
        Assert.Equal(["hello"], logged);
    }

    [Fact]
    public async Task Loose_subjects_return_defaults_without_setups()
    {
        var gateway = Subject().Create();

        Assert.Equal(string.Empty, gateway.Name);
        Assert.Null(await gateway.ChargeAsync("a", 1));
        Assert.Equal(0, await gateway.CountAsync("a"));
        Assert.Equal(0m, gateway.Price("a"));
        Assert.Empty(gateway.Skus());
        await gateway.RefundAsync("a");
        gateway.Log("x");
    }

    [Fact]
    public void Strict_subjects_throw_without_a_setup()
    {
        var gateway = Subject(strict: true).Setup(g => g.Log(Arg.Any<string>())).DoesNothing().Create();

        gateway.Log("fine");
        var error = Assert.Throws<ChaosSubjectException>(() => gateway.Price("a"));

        Assert.Contains("Price(\"a\")", error.Message);
    }

    [Fact]
    public void Matchers_and_the_last_matching_setup_win()
    {
        var gateway = Subject()
            .Setup(g => g.Price(Arg.Any<string>())).Returns(1m)
            .Setup(g => g.Price(Arg.Is<string>(s => s.StartsWith('v')))).Returns(2m)
            .Setup(g => g.Price("vip")).Returns(3m)
            .Create();

        Assert.Equal(1m, gateway.Price("basic"));
        Assert.Equal(2m, gateway.Price("value"));
        Assert.Equal(3m, gateway.Price("vip"));
    }

    [Fact]
    public void Literal_values_are_captured_from_variables()
    {
        var sku = "a";
        var gateway = Subject().Setup(g => g.Price(sku)).Returns(5m).Create();

        Assert.Equal(5m, gateway.Price("a"));
        Assert.Equal(0m, gateway.Price("b"));
    }

    [Fact]
    public void Array_and_list_arguments_match_by_content()
    {
        var gateway = Subject()
            .Setup(g => g.Format("x", 1, "a")).Returns("matched")
            .Setup(g => g.Total(new List<int> { 1, 2 })).Returns(3)
            .Create();

        Assert.Equal("matched", gateway.Format("x", 1, "a"));
        Assert.Equal(string.Empty, gateway.Format("x", 1, "b"));
        Assert.Equal(3, gateway.Total([1, 2]));
        Assert.Equal(0, gateway.Total([2, 1]));
    }

    [Fact]
    public async Task ReturnsInOrder_takes_plain_values_on_value_task_methods()
    {
        var gateway = Subject().Setup(g => g.CountAsync("a")).ReturnsInOrder(1, 2).Create();

        Assert.Equal(1, await gateway.CountAsync("a"));
        Assert.Equal(2, await gateway.CountAsync("a"));
        Assert.Equal(2, await gateway.CountAsync("a"));
    }

    [Fact]
    public async Task ReturnsInOrder_repeats_the_last_value()
    {
        var gateway = Subject()
            .Setup(g => g.Price("a")).ReturnsInOrder(1m, 2m)
            .Setup(g => g.ChargeAsync("a", 1m)).ReturnsInOrder(new Receipt("1", 1), new Receipt("2", 2))
            .Create();

        Assert.Equal([1m, 2m, 2m], new[] { gateway.Price("a"), gateway.Price("a"), gateway.Price("a") });
        Assert.Equal("1", (await gateway.ChargeAsync("a", 1m)).Sku);
        Assert.Equal("2", (await gateway.ChargeAsync("a", 1m)).Sku);
        Assert.Equal("2", (await gateway.ChargeAsync("a", 1m)).Sku);
    }

    [Fact]
    public async Task Throws_is_synchronous_for_sync_methods_and_faulted_for_async_ones()
    {
        var gateway = Subject()
            .Setup(g => g.Price(Arg.Any<string>())).Throws<InvalidOperationException>()
            .Setup(g => g.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>())).Throws(() => new TimeoutException("slow"))
            .Setup(g => g.RefundAsync(Arg.Any<string>())).Throws<NotSupportedException>()
            .Setup(g => g.Log(Arg.Any<string>())).Throws<ArgumentException>()
            .Create();

        Assert.Throws<InvalidOperationException>(() => gateway.Price("a"));
        var charge = gateway.ChargeAsync("a", 1);
        Assert.Equal("slow", (await Assert.ThrowsAsync<TimeoutException>(() => charge)).Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RefundAsync("a"));
        Assert.Throws<ArgumentException>(() => gateway.Log("x"));
    }

    [Fact]
    public void Callbacks_run_before_the_result()
    {
        var seen = new List<string>();
        var gateway = Subject().Setup(g => g.Price(Arg.Any<string>())).Callback<string>(seen.Add).Returns(4m).Create();

        Assert.Equal(4m, gateway.Price("a"));
        Assert.Equal(["a"], seen);
    }

    [Fact]
    public async Task When_expressions_limit_chaos_to_matching_calls_and_chaos_runs_before_behaviour()
    {
        var subject = Subject()
            .Setup(g => g.ChargeAsync(Arg.Any<string>(), Arg.Any<decimal>())).Returns(new Receipt("ok", 1))
            .Setup(g => g.Price(Arg.Any<string>())).Returns(1m)
            .When(g => g.ChargeAsync(Arg.Any<string>(), Arg.Is<decimal>(a => a > 100)))
            .ForCalls(2).Fail<TimeoutException>();
        var gateway = subject.Create();

        Assert.Equal("ok", (await gateway.ChargeAsync("a", 5)).Sku);
        await Assert.ThrowsAsync<TimeoutException>(() => gateway.ChargeAsync("a", 500));
        Assert.Equal(1m, gateway.Price("a"));
        await Assert.ThrowsAsync<TimeoutException>(() => gateway.ChargeAsync("a", 500));
        Assert.Equal("ok", (await gateway.ChargeAsync("a", 500)).Sku);

        subject.Verify().FaultsInjected(exactly: 2);
    }

    [Fact]
    public async Task Freeze_holds_mock_calls_until_the_window_ends()
    {
        var gateway = Subject().Setup(g => g.CountAsync("a")).Returns(1).For(5, TimeUnit.Seconds).Freeze().Create();

        var count = gateway.CountAsync("a").AsTask();
        Assert.False(count.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await count);
    }

    [Fact]
    public void Partial_mocks_send_calls_without_a_setup_to_the_real_object()
    {
        var real = new RealGateway();
        var gateway = Subject().Setup(g => g.Price("a")).Returns(100m).Create(real);

        Assert.Equal(100m, gateway.Price("a"));
        Assert.Equal(1m, gateway.Price("b"));
        Assert.Equal("real", gateway.Name);
    }

    [Fact]
    public void Received_counts_matching_calls_including_ones_chaos_failed()
    {
        var subject = Subject().ForCalls(1).Fail<TimeoutException>();
        var gateway = subject.Create();

        Assert.Throws<TimeoutException>(() => gateway.Price("a"));
        gateway.Price("a");
        gateway.Price("b");
        gateway.Log("x");

        subject
            .Received(g => g.Price("a"), Times.Exactly(2))
            .Received(g => g.Price(Arg.Any<string>()), Times.AtLeast(3))
            .Received(g => g.Log(Arg.Any<string>()), Times.Once)
            .DidNotReceive(g => g.Skus());
        var error = Assert.Throws<ChaosAssertionException>(() => subject.Received(g => g.Price("b"), Times.Never));
        Assert.Contains("Expected exactly 0 call(s)", error.Message);
        Assert.Contains("Price(\"b\")", error.Message);
        Assert.Equal(4, subject.Calls.Count);
    }

    [Fact]
    public void Only_interfaces_can_be_subjects()
    {
        Assert.Throws<ArgumentException>(() => new ChaosSubject<RealGateway>());
    }

    [Fact]
    public void Setups_must_call_the_subject()
    {
        Assert.Throws<ArgumentException>(() => Subject().Setup(g => "not a call"));
    }

    [Fact]
    public async Task ReturnOddValues_overrides_setups_with_odd_values()
    {
        var gateway = Subject()
            .Setup(g => g.Price(Arg.Any<string>())).Returns(1m)
            .Setup(g => g.Name).Returns("stripe")
            .Forever().ReturnOddValues()
            .Create();

        var prices = Enumerable.Range(0, 30).Select(_ => gateway.Price("a")).ToHashSet();
        var names = Enumerable.Range(0, 30).Select(_ => (string?)gateway.Name).ToHashSet();

        Assert.DoesNotContain(1m, prices);
        Assert.Contains(null, names);
        Assert.DoesNotContain("stripe", names);
        await gateway.RefundAsync("a");
        gateway.Log("void methods pass through");
    }

    [Fact]
    public void ProxyFactory_can_return_odd_values_from_a_real_object()
    {
        var gateway = new ProxyFactory<IPaymentGateway>(_clock, seed: 3).Forever().ReturnOddValues().Create(new RealGateway());

        var prices = Enumerable.Range(0, 30).Select(_ => gateway.Price("a")).ToHashSet();

        Assert.DoesNotContain(1m, prices);
        Assert.True(prices.Count > 1);
    }

    [Fact]
    public void Monkey_breaks_subjects_including_with_odd_values()
    {
        var monkey = new ChaosMonkey(_clock, seed: 4);
        var subject = new ChaosSubject<IPaymentGateway>(monkey).Named("gateway");

        monkey.Start();

        Assert.NotEmpty(monkey.Plan.Incidents);
        Assert.Contains(subject.MonkeyFaults, f => f.Name == "ReturnOddValues");
        Assert.Contains(new ProxyFactory<IPaymentGateway>().MonkeyFaults, f => f.Name == "ReturnOddValues");
    }

    private sealed class RealGateway : IPaymentGateway
    {
        public string Name => "real";

        public Task<Receipt> ChargeAsync(string sku, decimal amount) => Task.FromResult(new Receipt(sku, amount));

        public ValueTask<int> CountAsync(string sku) => new(1);

        public Task RefundAsync(string sku) => Task.CompletedTask;

        public ValueTask PingAsync() => ValueTask.CompletedTask;

        public decimal Price(string sku) => 1m;

        public void Log(string message)
        {
        }

        public IReadOnlyList<string> Skus() => ["real"];

        public T Get<T>(string key) => default!;

        public string Format(string template, params object[] args) => template;

        public int Total(IReadOnlyList<int> values) => values.Sum();
    }
}

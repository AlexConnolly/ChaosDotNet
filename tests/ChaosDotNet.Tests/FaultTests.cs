namespace ChaosDotNet.Tests;

public sealed class FaultTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly Inventory _inner = new();

    [Fact]
    public async Task Freeze_holds_calls_until_the_window_ends_then_lets_them_through()
    {
        var factory = new ProxyFactory<IInventory>(_clock).For(10, TimeUnit.Seconds).Freeze();
        var veneer = factory.Create(_inner);

        var call = veneer.CountAsync("a", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(9));

        Assert.False(call.IsCompleted);
        Assert.Equal(1, factory.Engine.FrozenCalls);

        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(42, await call);
        Assert.Equal(0, factory.Engine.FrozenCalls);
    }

    [Fact]
    public async Task Freeze_honours_the_call_cancellation_token()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).For(10, TimeUnit.Seconds).Freeze().Create(_inner);
        using var cts = new CancellationTokenSource();

        var call = veneer.CountAsync("a", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task Unbounded_freeze_throws_after_the_max_freeze()
    {
        var veneer = new ProxyFactory<IInventory>(_clock)
            .WithMaxFreeze(5, TimeUnit.Seconds)
            .Forever().Freeze()
            .Create(_inner);

        var call = veneer.CountAsync("a", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ChaosFreezeTimeoutException>(() => call);
        Assert.Equal(TimeSpan.FromSeconds(5), error.MaxFreeze);
    }

    [Fact]
    public async Task ForCalls_freeze_hangs_those_calls_only()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).WithMaxFreeze(1, TimeUnit.Seconds).ForCalls(1).Freeze().Create(_inner);

        var hung = veneer.CountAsync("a", TestContext.Current.CancellationToken);
        var next = await veneer.CountAsync("a", TestContext.Current.CancellationToken);

        Assert.Equal(42, next);
        Assert.False(hung.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ChaosFreezeTimeoutException>(() => hung);
    }

    [Fact]
    public async Task Until_freeze_releases_when_the_condition_becomes_true()
    {
        var healthy = false;
        var veneer = new ProxyFactory<IInventory>(_clock).Until(() => healthy).Freeze().Create(_inner);

        var call = veneer.CountAsync("a", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(call.IsCompleted);

        healthy = true;
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(42, await call);
    }

    [Fact]
    public async Task Latency_delays_each_call()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).Forever().Latency(2, TimeUnit.Seconds).Create(_inner);

        var call = veneer.CountAsync("a", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(call.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(0.1));

        Assert.Equal(42, await call);
    }

    [Fact]
    public async Task Jitter_delays_between_the_bounds()
    {
        var veneer = new ProxyFactory<IInventory>(_clock, seed: 3).Forever().Jitter(1, 3, TimeUnit.Seconds).Create(_inner);

        var calls = Enumerable.Range(0, 20).Select(_ => veneer.CountAsync("a", TestContext.Current.CancellationToken)).ToArray();
        _clock.Advance(TimeSpan.FromSeconds(0.999));
        Assert.All(calls, c => Assert.False(c.IsCompleted));

        _clock.Advance(TimeSpan.FromSeconds(1));
        var early = calls.Count(c => c.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1.001));

        Assert.InRange(early, 1, 19);
        Assert.All(await Task.WhenAll(calls), r => Assert.Equal(42, r));
    }

    [Fact]
    public void Fail_creates_a_new_exception_per_call()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).Forever().Fail(() => new ChaosTestException()).Create(_inner);

        var first = Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        var second = Assert.Throws<ChaosTestException>(() => veneer.Count("a"));

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Fail_can_build_the_exception_from_the_call()
    {
        var veneer = new ProxyFactory<IInventory>(_clock)
            .Forever().Fail(call => new InvalidOperationException((string)call.Arguments[0]!))
            .Create(_inner);

        var error = Assert.Throws<InvalidOperationException>(() => veneer.Count("sku-9"));

        Assert.Equal("sku-9", error.Message);
    }
}

namespace ChaosDotNet.Tests;

public sealed class ProxyFactoryTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly Inventory _inner = new();

    [Fact]
    public async Task Every_return_shape_passes_through()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).Create(_inner);

        Assert.Equal(42, veneer.Count("a"));
        Assert.Equal(42, await veneer.CountAsync("a", TestContext.Current.CancellationToken));
        await veneer.ReserveAsync("a", TestContext.Current.CancellationToken);
        Assert.Equal(42, await veneer.PeekAsync("a"));
        await veneer.ReleaseAsync("a");

        Assert.Equal(5, _inner.Calls);
    }

    [Fact]
    public async Task Every_return_shape_fails()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).Forever().Fail<ChaosTestException>().Create(_inner);

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        await Assert.ThrowsAsync<ChaosTestException>(() => veneer.CountAsync("a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ChaosTestException>(() => veneer.ReserveAsync("a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ChaosTestException>(async () => await veneer.PeekAsync("a"));
        await Assert.ThrowsAsync<ChaosTestException>(async () => await veneer.ReleaseAsync("a"));

        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task Target_exceptions_surface_unwrapped()
    {
        var veneer = new ProxyFactory<IInventory>(_clock).Create(_inner);
        _inner.Throw = new ChaosTestException();

        Assert.Throws<ChaosTestException>(() => veneer.Count("a"));
        await Assert.ThrowsAsync<ChaosTestException>(() => veneer.CountAsync("a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Operation_drops_the_async_suffix()
    {
        var factory = new ProxyFactory<IInventory>(_clock).ForCalls(1).Fail<ChaosTestException>();
        var veneer = factory.Create(_inner);

        await Assert.ThrowsAsync<ChaosTestException>(() => veneer.CountAsync("a", TestContext.Current.CancellationToken));

        Assert.Equal("Count", factory.Log.Single(e => e.Kind == ChaosEventKind.FaultInjected).Operation);
    }

    [Fact]
    public void Only_interfaces_can_be_wrapped()
    {
        Assert.Throws<ArgumentException>(() => new ProxyFactory<Inventory>(_clock).Create(_inner));
    }

    [Fact]
    public void Veneers_from_one_factory_share_the_timeline()
    {
        var factory = new ProxyFactory<IInventory>(_clock).ForCalls(2).Fail<ChaosTestException>();
        var first = factory.Create(_inner);
        var second = factory.Create(_inner);

        Assert.Throws<ChaosTestException>(() => first.Count("a"));
        Assert.Throws<ChaosTestException>(() => second.Count("a"));
        Assert.Equal(42, first.Count("a"));
    }

    [Fact]
    public async Task Parallel_factories_are_isolated()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            var inner = new Inventory();
            var factory = new ProxyFactory<IInventory>(new FakeTimeProvider());
            var veneer = (i % 2 == 0 ? factory.Forever().Fail<ChaosTestException>() : factory).Create(inner);
            try
            {
                veneer.Count("a");
                return (i, failed: false);
            }
            catch (ChaosTestException)
            {
                return (i, failed: true);
            }
        })));

        Assert.All(results, r => Assert.Equal(r.i % 2 == 0, r.failed));
    }
}

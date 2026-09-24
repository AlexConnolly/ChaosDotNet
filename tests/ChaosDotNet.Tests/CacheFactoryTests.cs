using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ChaosDotNet.Tests;

public sealed class CacheFactoryTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task Calls_pass_through()
    {
        var cache = new CacheFactory(_clock).Create(_inner);

        await cache.SetStringAsync("k", "v", TestContext.Current.CancellationToken);

        Assert.Equal("v", await cache.GetStringAsync("k", TestContext.Current.CancellationToken));
        Assert.Equal("v", cache.GetString("k"));
    }

    [Fact]
    public async Task Miss_hides_values_only_from_reads()
    {
        var factory = new CacheFactory(_clock).For(1, TimeUnit.Minutes).Miss();
        var cache = factory.Create(_inner);

        await cache.SetStringAsync("k", "v", TestContext.Current.CancellationToken);
        Assert.Null(await cache.GetStringAsync("k", TestContext.Current.CancellationToken));
        Assert.Null(cache.GetString("k"));
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("v", await cache.GetStringAsync("k", TestContext.Current.CancellationToken));
        factory.Verify().FaultsInjected(exactly: 2);
    }

    [Fact]
    public async Task Timeout_fails_every_operation()
    {
        var cache = new CacheFactory(_clock).Forever().Timeout().Create(_inner);

        await Assert.ThrowsAsync<TimeoutException>(() => cache.GetAsync("k", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => cache.SetAsync("k", [1], new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => cache.RefreshAsync("k", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => cache.RemoveAsync("k", TestContext.Current.CancellationToken));
        Assert.Throws<TimeoutException>(() => cache.Get("k"));
        Assert.Throws<TimeoutException>(() => cache.Set("k", [1], new DistributedCacheEntryOptions()));
        Assert.Throws<TimeoutException>(() => cache.Refresh("k"));
        Assert.Throws<TimeoutException>(() => cache.Remove("k"));
    }

    [Fact]
    public async Task When_filters_on_key_and_operation()
    {
        var cache = new CacheFactory(_clock)
            .When(call => call.Operation == "Set" && call.Key.StartsWith("hot:", StringComparison.Ordinal))
            .Forever().Timeout()
            .Create(_inner);

        await cache.SetStringAsync("cold:1", "v", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => cache.SetStringAsync("hot:1", "v", TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetStringAsync("hot:1", TestContext.Current.CancellationToken));
    }
}

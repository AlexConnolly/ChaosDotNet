using Microsoft.Extensions.Caching.Distributed;

namespace ChaosDotNet.Factories;

/// <summary>A cache call made through a <see cref="CacheFactory"/> veneer.</summary>
public sealed class CacheChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    /// <param name="operation">One of <c>Get</c>, <c>Set</c>, <c>Refresh</c> or <c>Remove</c>.</param>
    /// <param name="key">The cache key.</param>
    public CacheChaosCall(string operation, string key)
        : base(operation)
    {
        Key = key;
    }

    /// <summary>The cache key.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public override string? Details => Key;
}

/// <summary>
/// Builds <see cref="IDistributedCache"/> veneers. <c>HybridCache</c> and output caching use <see cref="IDistributedCache"/> as their
/// second level, so the veneer also works under them.
/// </summary>
/// <example>
/// <code>
/// var cache = new CacheFactory().For(1, TimeUnit.Minutes).Miss();
/// services.AddSingleton&lt;IDistributedCache&gt;(cache.Create(new MemoryDistributedCache(...)));
/// </code>
/// </example>
public sealed class CacheFactory : ChaosFactory<CacheFactory, CacheChaosCall>
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public CacheFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public CacheFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates an <see cref="IDistributedCache"/> veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public IDistributedCache Create(IDistributedCache inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var veneer = new ChaosDistributedCache(inner, Engine);
        Engine.Start();
        return veneer;
    }

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() =>
    [
        .. base.DefaultMonkeyFaults(),
        new("Timeout", MonkeyFaultKind.Error, _ => new FailFault(call => CacheFaults.TimeoutException((CacheChaosCall)call)), 2),
        new("Corrupt", MonkeyFaultKind.Weird, random => new CorruptFault(new Random(random.Next()))),
        new("Miss", MonkeyFaultKind.DataLoss, _ => MissFault.Instance),
    ];
}

/// <summary>Cache faults for a <see cref="CacheFactory"/> window.</summary>
public static class CacheFaults
{
    /// <summary><c>Get</c> calls return <see langword="null"/> without reaching the cache. Other calls pass through.</summary>
    public static CacheFactory Miss(this WindowBuilder<CacheFactory, CacheChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(MissFault.Instance);
    }

    /// <summary>Each call fails with a <see cref="TimeoutException"/>.</summary>
    public static CacheFactory Timeout(this WindowBuilder<CacheFactory, CacheChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(TimeoutException);
    }

    /// <summary><c>Get</c> calls return random bytes instead of the stored value. Other calls pass through.</summary>
    public static CacheFactory Corrupt(this WindowBuilder<CacheFactory, CacheChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new CorruptFault(new Random(window.Factory.Seed)));
    }

    internal static Exception TimeoutException(CacheChaosCall call) =>
        new TimeoutException($"The cache did not respond to {call.Operation} '{call.Key}' in time.");
}

/// <summary>A fault that makes cache reads return random bytes.</summary>
public sealed class CorruptFault : Fault
{
    private readonly Random _random;

    /// <summary>Creates the fault.</summary>
    public CorruptFault(Random random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    /// <inheritdoc />
    public override string Name => "Corrupt";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call is CacheChaosCall { Operation: "Get" };

    /// <summary>The bytes a read returns.</summary>
    public byte[] NextValue()
    {
        var bytes = new byte[32];
        lock (_random)
        {
            _random.NextBytes(bytes);
        }

        return bytes;
    }
}

/// <summary>A fault that makes cache reads miss.</summary>
public sealed class MissFault : Fault
{
    internal static readonly MissFault Instance = new();

    private MissFault()
    {
    }

    /// <inheritdoc />
    public override string Name => "Miss";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call is CacheChaosCall { Operation: "Get" };
}

internal sealed class ChaosDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly ChaosEngine _engine;

    public ChaosDistributedCache(IDistributedCache inner, ChaosEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    public byte[]? Get(string key)
    {
        var call = new CacheChaosCall("Get", key);
        switch (_engine.BeforeCall(call))
        {
            case MissFault:
                return null;
            case CorruptFault corrupt:
                return corrupt.NextValue();
        }

        try
        {
            var value = _inner.Get(key);
            _engine.CallSucceeded(call);
            return value;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var call = new CacheChaosCall("Get", key);
        switch (await _engine.BeforeCallAsync(call, token).ConfigureAwait(false))
        {
            case MissFault:
                return null;
            case CorruptFault corrupt:
                return corrupt.NextValue();
        }

        try
        {
            var value = await _inner.GetAsync(key, token).ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return value;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        _engine.Run(new CacheChaosCall("Set", key), () => _inner.Set(key, value, options));

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        _engine.RunAsync(new CacheChaosCall("Set", key), () => _inner.SetAsync(key, value, options, token), token);

    public void Refresh(string key) =>
        _engine.Run(new CacheChaosCall("Refresh", key), () => _inner.Refresh(key));

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        _engine.RunAsync(new CacheChaosCall("Refresh", key), () => _inner.RefreshAsync(key, token), token);

    public void Remove(string key) =>
        _engine.Run(new CacheChaosCall("Remove", key), () => _inner.Remove(key));

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        _engine.RunAsync(new CacheChaosCall("Remove", key), () => _inner.RemoveAsync(key, token), token);
}

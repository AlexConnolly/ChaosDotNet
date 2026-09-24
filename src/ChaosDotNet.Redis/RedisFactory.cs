using System.Reflection;
using ChaosDotNet.Proxies;
using StackExchange.Redis;

namespace ChaosDotNet.Factories;

/// <summary>A Redis command made through a <see cref="RedisFactory"/> veneer.</summary>
public sealed class RedisChaosCall : ProxyChaosCall
{
    /// <summary>Creates a call.</summary>
    public RedisChaosCall(MethodInfo method, object?[] arguments)
        : base(method, arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is RedisKey key)
            {
                Key = key;
                break;
            }

            if (argument is RedisChannel channel)
            {
                Channel = channel;
                break;
            }
        }
    }

    /// <summary>The command's first key, for example <c>session:42</c>. <see langword="null"/> for commands without a key.</summary>
    public string? Key { get; }

    /// <summary>The pub/sub channel, for subscriber commands.</summary>
    public string? Channel { get; }
}

/// <summary>
/// Builds <see cref="IConnectionMultiplexer"/> veneers. Commands on the <see cref="IDatabase"/> and <see cref="ISubscriber"/>
/// objects the veneer returns go through the timeline. Batches and transactions pass through.
/// </summary>
/// <example>
/// <code>
/// var redis = new RedisFactory()
///     .When(call => call.Key?.StartsWith("session:") == true)
///     .For(30, TimeUnit.Seconds).Timeout();
///
/// IConnectionMultiplexer multiplexer = redis.Create(await ConnectionMultiplexer.ConnectAsync("localhost"));
/// </code>
/// </example>
public sealed class RedisFactory : ChaosFactory<RedisFactory, RedisChaosCall>
{
    private static readonly HashSet<string> PassThrough = ["CreateBatch", "CreateTransaction", "IsConnected", "IdentifyEndpoint", "IdentifyEndpointAsync"];

    /// <summary>Creates a factory with its own clock and seed.</summary>
    public RedisFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public RedisFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates an <see cref="IConnectionMultiplexer"/> veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public IConnectionMultiplexer Create(IConnectionMultiplexer inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        IConnectionMultiplexer? veneer = null;
        var commandOptions = new ChaosProxyOptions
        {
            Intercept = method => !method.IsSpecialName && !PassThrough.Contains(method.Name),
            CreateCall = (method, args) => new RedisChaosCall(method, args),
            WrapResult = (method, result) => method.Name == "get_Multiplexer" ? veneer : result,
            ApplyFault = RedisChaos.Apply,
        };

        veneer = ChaosProxy.Create(inner, Engine, new ChaosProxyOptions
        {
            Intercept = _ => false,
            WrapResult = (method, result) => result switch
            {
                IDatabase database when method.Name == nameof(IConnectionMultiplexer.GetDatabase) => ChaosProxy.Create(database, Engine, commandOptions),
                ISubscriber subscriber when method.Name == nameof(IConnectionMultiplexer.GetSubscriber) => ChaosProxy.Create(subscriber, Engine, commandOptions),
                _ => result,
            },
        });

        Engine.Start();
        return veneer;
    }

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() => [.. base.DefaultMonkeyFaults(), .. RedisChaos.Catalogue()];
}

/// <summary>Redis faults for a <see cref="RedisFactory"/> window.</summary>
public static class RedisFaults
{
    /// <summary>Each command fails with a <see cref="RedisTimeoutException"/>, as when the server does not reply in time.</summary>
    public static RedisFactory Timeout(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(RedisChaos.Timeout);
    }

    /// <summary>Each command fails with a <see cref="RedisConnectionException"/>, as when the socket to the server breaks.</summary>
    public static RedisFactory ConnectionFailure(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(RedisChaos.ConnectionFailure);
    }

    /// <summary>
    /// Each command fails with a <see cref="RedisServerException"/> picked at random from real server errors:
    /// <c>LOADING</c>, <c>READONLY</c>, <c>BUSY</c>, <c>MASTERDOWN</c>, <c>OOM</c>, <c>CLUSTERDOWN</c> and <c>TRYAGAIN</c>.
    /// </summary>
    public static RedisFactory ServerError(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.FailRandomly(RedisChaos.ServerErrors);
    }

    /// <summary>Reads that return a single value get random bytes instead of the stored value. Other commands pass through.</summary>
    public static RedisFactory CorruptValues(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new RedisValueFault(new Random(window.Factory.Seed), corrupt: true));
    }

    /// <summary>Reads that return a single value get a miss (no value) without reaching the server. Other commands pass through.</summary>
    public static RedisFactory Miss(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new RedisValueFault(new Random(window.Factory.Seed), corrupt: false));
    }
}

/// <summary>A fault that replaces the value a Redis read returns: random bytes, or a miss.</summary>
public sealed class RedisValueFault : Fault
{
    private readonly Random _random;

    /// <summary>Creates the fault.</summary>
    /// <param name="random">Builds the random bytes.</param>
    /// <param name="corrupt"><see langword="true"/> for random bytes, <see langword="false"/> for a miss.</param>
    public RedisValueFault(Random random, bool corrupt)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
        Corrupt = corrupt;
    }

    /// <summary><see langword="true"/> for random bytes, <see langword="false"/> for a miss.</summary>
    public bool Corrupt { get; }

    /// <inheritdoc />
    public override string Name => Corrupt ? "CorruptValues" : "Miss";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) =>
        call is RedisChaosCall redis && (redis.Method.ReturnType == typeof(RedisValue) || redis.Method.ReturnType == typeof(Task<RedisValue>));

    /// <summary>The value the read returns.</summary>
    public RedisValue NextValue()
    {
        if (!Corrupt)
        {
            return RedisValue.Null;
        }

        var bytes = new byte[24];
        lock (_random)
        {
            _random.NextBytes(bytes);
        }

        return bytes;
    }
}

internal static class RedisChaos
{
    public static readonly Func<Exception>[] ServerErrors =
    [
        () => new RedisServerException("LOADING Redis is loading the dataset in memory"),
        () => new RedisServerException("READONLY You can't write against a read only replica."),
        () => new RedisServerException("BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE."),
        () => new RedisServerException("MASTERDOWN Link with MASTER is down and replica-serve-stale-data is set to 'no'."),
        () => new RedisServerException("OOM command not allowed when used memory > 'maxmemory'."),
        () => new RedisServerException("CLUSTERDOWN The cluster is down"),
        () => new RedisServerException("TRYAGAIN Multiple keys request during rehashing of slot"),
    ];

    public static Exception Timeout(RedisChaosCall call) => new RedisTimeoutException(
        $"Timeout performing {call.Operation.ToUpperInvariant()} ({call.Key ?? call.Channel ?? "no key"}), inst: 1, qu: 0, qs: 1, aw: False, active: {call.Operation}, rs: ReadAsync, ws: Idle (ChaosDotNet)",
        CommandStatus.Sent);

    public static Exception ConnectionFailure(RedisChaosCall call) => new RedisConnectionException(
        ConnectionFailureType.SocketFailure,
        $"No connection is active/available to service this operation: {call.Operation.ToUpperInvariant()} {call.Key} (ChaosDotNet)");

    public static object? Apply(Fault fault, System.Reflection.MethodInfo method, object?[] args)
    {
        if (fault is not RedisValueFault value)
        {
            throw new NotSupportedException($"The fault '{fault.Name}' is not supported by RedisFactory.");
        }

        var result = value.NextValue();
        return method.ReturnType == typeof(RedisValue) ? result : Task.FromResult(result);
    }

    public static IEnumerable<MonkeyFault> Catalogue() =>
    [
        new("ConnectionFailure", MonkeyFaultKind.Outage, _ => new FailFault(call => ConnectionFailure((RedisChaosCall)call)), 2),
        new("Timeout", MonkeyFaultKind.Error, _ => new FailFault(call => Timeout((RedisChaosCall)call)), 2),
        MonkeyFault.RandomException("ServerError", MonkeyFaultKind.Error, ServerErrors),
        new("CorruptValues", MonkeyFaultKind.Weird, random => new RedisValueFault(new Random(random.Next()), corrupt: true)),
        new("Miss", MonkeyFaultKind.DataLoss, random => new RedisValueFault(new Random(random.Next()), corrupt: false)),
    ];
}

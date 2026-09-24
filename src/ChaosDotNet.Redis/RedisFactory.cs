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
}

/// <summary>Redis faults for a <see cref="RedisFactory"/> window.</summary>
public static class RedisFaults
{
    /// <summary>Each command fails with a <see cref="RedisTimeoutException"/>, as when the server does not reply in time.</summary>
    public static RedisFactory Timeout(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(call => new RedisTimeoutException(
            $"Timeout performing {call.Operation.ToUpperInvariant()} ({call.Key ?? call.Channel ?? "no key"}), inst: 1, qu: 0, qs: 1, aw: False, active: {call.Operation}, rs: ReadAsync, ws: Idle (ChaosDotNet)",
            CommandStatus.Sent));
    }

    /// <summary>Each command fails with a <see cref="RedisConnectionException"/>, as when the socket to the server breaks.</summary>
    public static RedisFactory ConnectionFailure(this WindowBuilder<RedisFactory, RedisChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(call => new RedisConnectionException(
            ConnectionFailureType.SocketFailure,
            $"No connection is active/available to service this operation: {call.Operation.ToUpperInvariant()} {call.Key} (ChaosDotNet)"));
    }
}

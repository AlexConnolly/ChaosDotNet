using ChaosDotNet.Factories;
using StackExchange.Redis;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Redis support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesRedisExtensions
{
    /// <summary>Wraps the registered <see cref="IConnectionMultiplexer"/> with a <see cref="RedisFactory"/> named <c>redis</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder Redis(this ChaosServicesBuilder builder, Action<RedisFactory>? configure = null) =>
        builder.Redis(ChaosStrategy.Proxy, null, configure);

    /// <summary>Adds chaos to <see cref="IConnectionMultiplexer"/> with a <see cref="RedisFactory"/> named <c>redis</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps the app's multiplexer. <see cref="ChaosStrategy.Replace"/> removes it and
    /// connects a new <see cref="ConnectionMultiplexer"/> with <paramref name="configuration"/>.
    /// </param>
    /// <param name="configuration">The Redis configuration string, for example <c>localhost:6379</c>. Required for <see cref="ChaosStrategy.Replace"/>.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder Redis(this ChaosServicesBuilder builder, ChaosStrategy strategy, string? configuration = null, Action<RedisFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (strategy == ChaosStrategy.Replace)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        }

        return builder.Add(() =>
        {
            if (builder.IsExcluded("redis"))
            {
                return;
            }

            var factory = new RedisFactory(builder.Scenario).Named("redis");
            configure?.Invoke(factory);
            if (strategy == ChaosStrategy.Replace)
            {
                builder.Replace<IConnectionMultiplexer>(_ => factory.Create(ConnectionMultiplexer.Connect(configuration!)));
            }
            else
            {
                builder.Decorate<IConnectionMultiplexer>((_, inner) => factory.Create(inner));
            }

            builder.Track("redis");
        });
    }
}

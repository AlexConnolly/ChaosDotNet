using ChaosDotNet.Factories;
using StackExchange.Redis;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Redis support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesRedisExtensions
{
    /// <summary>Wraps the registered <see cref="IConnectionMultiplexer"/> with a <see cref="RedisFactory"/> named <c>redis</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder Redis(this ChaosServicesBuilder builder, Action<RedisFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(() =>
        {
            if (builder.IsExcluded("redis"))
            {
                return;
            }

            var factory = new RedisFactory(builder.Scenario).Named("redis");
            configure?.Invoke(factory);
            builder.Decorate<IConnectionMultiplexer>((_, inner) => factory.Create(inner));
            builder.Track("redis");
        });
    }
}

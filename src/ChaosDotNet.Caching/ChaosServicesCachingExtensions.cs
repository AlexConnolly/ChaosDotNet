using ChaosDotNet.Factories;
using Microsoft.Extensions.Caching.Distributed;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Cache support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesCachingExtensions
{
    /// <summary>Wraps the registered <see cref="IDistributedCache"/> with a <see cref="CacheFactory"/> named <c>cache</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder DistributedCache(this ChaosServicesBuilder builder, Action<CacheFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(() =>
        {
            if (builder.IsExcluded("cache"))
            {
                return;
            }

            var factory = new CacheFactory(builder.Scenario).Named("cache");
            configure?.Invoke(factory);
            builder.Decorate<IDistributedCache>((_, inner) => factory.Create(inner));
            builder.Track("cache");
        });
    }
}

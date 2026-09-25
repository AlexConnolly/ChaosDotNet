using ChaosDotNet.Factories;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Cache support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesCachingExtensions
{
    /// <summary>Wraps the registered <see cref="IDistributedCache"/> with a <see cref="CacheFactory"/> named <c>cache</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder DistributedCache(this ChaosServicesBuilder builder, Action<CacheFactory>? configure = null) =>
        builder.DistributedCache(ChaosStrategy.Proxy, configure);

    /// <summary>Adds chaos to <see cref="IDistributedCache"/> with a <see cref="CacheFactory"/> named <c>cache</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps the app's cache. <see cref="ChaosStrategy.Replace"/> removes it and uses an
    /// in-memory <see cref="MemoryDistributedCache"/>, so no cache server is needed.
    /// </param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder DistributedCache(this ChaosServicesBuilder builder, ChaosStrategy strategy, Action<CacheFactory>? configure = null)
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
            if (strategy == ChaosStrategy.Replace)
            {
                builder.Replace<IDistributedCache>(_ => factory.Create(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))));
            }
            else
            {
                builder.Decorate<IDistributedCache>((_, inner) => factory.Create(inner));
            }

            builder.Track("cache");
        });
    }
}

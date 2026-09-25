using ChaosDotNet.Factories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChaosDotNet.DependencyInjection;

/// <summary>EF Core support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Adds a <see cref="SqlFactory"/> interceptor to every registered <see cref="DbContext"/>. Each context gets its own factory,
    /// named <c>sql:{context type}</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives each context's factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder EntityFrameworkCore(this ChaosServicesBuilder builder, Action<Type, SqlFactory>? configure = null) =>
        builder.EntityFrameworkCore(ChaosStrategy.Proxy, configure);

    /// <summary>
    /// Adds a <see cref="SqlFactory"/> interceptor to every registered <see cref="DbContext"/>. Each context gets its own factory,
    /// named <c>sql:{context type}</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// Only <see cref="ChaosStrategy.Proxy"/>: a context's provider setup belongs to the app, so it cannot be replaced. To drop
    /// the app's own connection logic, replace the data source instead, for example with <c>Npgsql(ChaosStrategy.Replace, ...)</c>.
    /// </param>
    /// <param name="configure">Gives each context's factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder EntityFrameworkCore(this ChaosServicesBuilder builder, ChaosStrategy strategy, Action<Type, SqlFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (strategy == ChaosStrategy.Replace)
        {
            throw new NotSupportedException(
                "EF Core contexts can only be proxied: their provider setup belongs to the app. To drop the app's connection logic, replace the data source instead, for example Npgsql(ChaosStrategy.Replace, connectionString).");
        }

        return builder.Add(() =>
        {
            var contexts = builder.Services
                .Select(d => d.ServiceType)
                .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
                .Select(t => t.GetGenericArguments()[0])
                .Distinct()
                .ToList();
            if (contexts.Count == 0)
            {
                throw new InvalidOperationException("No DbContext is registered, so there is nothing to wrap.");
            }

            foreach (var context in contexts)
            {
                var name = $"sql:{context.Name}";
                if (builder.IsExcluded(name))
                {
                    continue;
                }

                var factory = new SqlFactory(builder.Scenario).Named(name);
                configure?.Invoke(context, factory);
                typeof(ChaosServicesEntityFrameworkCoreExtensions)
                    .GetMethod(nameof(Intercept), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(context)
                    .Invoke(null, [builder, factory]);
                builder.Track(name);
            }
        });
    }

    private static void Intercept<TContext>(ChaosServicesBuilder builder, SqlFactory factory)
        where TContext : DbContext
    {
        IInterceptor? interceptor = null;
        builder.Decorate<DbContextOptions<TContext>>((_, options) =>
            new DbContextOptionsBuilder<TContext>(options).AddInterceptors(interceptor ??= factory.CreateInterceptor()).Options);
    }
}

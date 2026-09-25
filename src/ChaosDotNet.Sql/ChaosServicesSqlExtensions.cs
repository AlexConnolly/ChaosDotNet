using System.Data.Common;
using ChaosDotNet.Factories;
using ChaosDotNet.Sql;
using Microsoft.Extensions.DependencyInjection;

namespace ChaosDotNet.DependencyInjection;

/// <summary>ADO.NET support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesSqlExtensions
{
    /// <summary>Adds chaos to the app's <see cref="DbDataSource"/> and <see cref="DbConnection"/> registrations with one <see cref="SqlFactory"/>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps each registered <see cref="DbDataSource"/> and <see cref="DbConnection"/>.
    /// <see cref="ChaosStrategy.Replace"/> removes them and registers the data source from <paramref name="dataSource"/>,
    /// plus a transient <see cref="DbConnection"/> from it.
    /// </param>
    /// <param name="dataSource">Creates the data source that replaces the app's. Required for <see cref="ChaosStrategy.Replace"/>.</param>
    /// <param name="configure">Gives the factory a hand-written timeline or provider faults. Leave it out to let the monkey decide.</param>
    /// <param name="name">The factory's name. Defaults to <c>sql</c>.</param>
    public static ChaosServicesBuilder Sql(
        this ChaosServicesBuilder builder,
        ChaosStrategy strategy = ChaosStrategy.Proxy,
        Func<IServiceProvider, DbDataSource>? dataSource = null,
        Action<SqlFactory>? configure = null,
        string name = "sql")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (strategy == ChaosStrategy.Replace)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
        }

        return builder.Add(() =>
        {
            if (builder.IsExcluded(name))
            {
                return;
            }

            var factory = new SqlFactory(builder.Scenario).Named(name);
            configure?.Invoke(factory);
            if (strategy == ChaosStrategy.Replace)
            {
                builder.Replace<DbDataSource>(provider => factory.CreateDataSource(dataSource!(provider)));
                builder.Replace<DbConnection>(provider => provider.GetRequiredService<DbDataSource>().CreateConnection(), ServiceLifetime.Transient);
            }
            else
            {
                var dataSources = Has<DbDataSource>(builder.Services);
                var connections = Has<DbConnection>(builder.Services);
                if (!dataSources && !connections)
                {
                    throw new InvalidOperationException(
                        "No DbDataSource or DbConnection is registered, so there is nothing to wrap. Call AddChaosMonkey after the app registers its services, or use ChaosStrategy.Replace.");
                }

                if (dataSources)
                {
                    builder.Decorate<DbDataSource>((_, inner) => inner is ChaosDbDataSource ? inner : factory.CreateDataSource(inner));
                }

                if (connections)
                {
                    // A connection registered from the data source is already a veneer; wrapping it again would count every call twice.
                    builder.Decorate<DbConnection>((_, inner) => inner is ChaosDbConnection ? inner : factory.CreateConnection(inner));
                }
            }

            builder.Track(name);
        });
    }

    private static bool Has<T>(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
}

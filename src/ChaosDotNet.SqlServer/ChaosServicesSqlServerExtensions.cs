using System.Data.Common;
using ChaosDotNet.Factories;
using Microsoft.Data.SqlClient;

namespace ChaosDotNet.DependencyInjection;

/// <summary>SQL Server support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesSqlServerExtensions
{
    /// <summary>
    /// Adds chaos to the app's SQL Server <see cref="DbDataSource"/> and <see cref="DbConnection"/> registrations with a
    /// <see cref="SqlFactory"/> named <c>sqlserver</c> that uses SQL Server errors.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps the app's registrations, so its own connection logic still runs.
    /// <see cref="ChaosStrategy.Replace"/> removes them and uses a standard SqlClient data source built from
    /// <paramref name="connectionString"/>.
    /// </param>
    /// <param name="connectionString">The SQL Server connection string. Required for <see cref="ChaosStrategy.Replace"/>.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder SqlServer(this ChaosServicesBuilder builder, ChaosStrategy strategy = ChaosStrategy.Proxy, string? connectionString = null, Action<SqlFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (strategy == ChaosStrategy.Replace)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        }

        return builder.Sql(
            strategy,
            strategy == ChaosStrategy.Replace ? _ => SqlClientFactory.Instance.CreateDataSource(connectionString!) : null,
            factory =>
            {
                factory.UseSqlServerFaults();
                configure?.Invoke(factory);
            },
            "sqlserver");
    }
}

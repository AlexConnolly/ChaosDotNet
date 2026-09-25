using System.Data.Common;
using ChaosDotNet.Factories;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChaosDotNet.DependencyInjection;

/// <summary>PostgreSQL support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesNpgsqlExtensions
{
    /// <summary>
    /// Adds chaos to the app's PostgreSQL <see cref="DbDataSource"/> and <see cref="DbConnection"/> registrations with a
    /// <see cref="SqlFactory"/> named <c>postgres</c> that uses PostgreSQL errors.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps the app's registrations, so its own pooler or data source still runs.
    /// <see cref="ChaosStrategy.Replace"/> removes the app's <see cref="DbDataSource"/>, <see cref="DbConnection"/>,
    /// <see cref="NpgsqlDataSource"/> and <see cref="NpgsqlConnection"/> registrations and uses a standard
    /// <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/>. Only code that asks for
    /// <see cref="DbDataSource"/> or <see cref="DbConnection"/> gets chaos; the concrete Npgsql types cannot be wrapped.
    /// </param>
    /// <param name="connectionString">The PostgreSQL connection string. Required for <see cref="ChaosStrategy.Replace"/>.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder Npgsql(this ChaosServicesBuilder builder, ChaosStrategy strategy = ChaosStrategy.Proxy, string? connectionString = null, Action<SqlFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (strategy == ChaosStrategy.Proxy)
        {
            return builder.Sql(ChaosStrategy.Proxy, configure: Configure, name: "postgres");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Add(() =>
        {
            if (builder.IsExcluded("postgres"))
            {
                return;
            }

            builder.Replace(_ => NpgsqlDataSource.Create(connectionString));
            builder.Replace(provider => provider.GetRequiredService<NpgsqlDataSource>().CreateConnection(), ServiceLifetime.Transient);
        });
        return builder.Sql(ChaosStrategy.Replace, provider => provider.GetRequiredService<NpgsqlDataSource>(), Configure, "postgres");

        void Configure(SqlFactory factory)
        {
            factory.UseNpgsqlFaults();
            configure?.Invoke(factory);
        }
    }
}

using System.Data.Common;
using ChaosDotNet.Sql;

namespace ChaosDotNet.Factories;

/// <summary>A database call made through a <see cref="SqlFactory"/> veneer.</summary>
public sealed class SqlChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    /// <param name="operation">One of <c>Open</c>, <c>BeginTransaction</c>, <c>ExecuteReader</c>, <c>ExecuteNonQuery</c>, <c>ExecuteScalar</c>, <c>Commit</c> or <c>Rollback</c>.</param>
    /// <param name="commandText">The command text, for command calls.</param>
    public SqlChaosCall(string operation, string? commandText = null)
        : base(operation)
    {
        CommandText = commandText;
    }

    /// <summary>The command text, for command calls. <see langword="null"/> for connection and transaction calls.</summary>
    public string? CommandText { get; }

    /// <summary><see langword="true"/> for <c>ExecuteReader</c>, <c>ExecuteNonQuery</c> and <c>ExecuteScalar</c>.</summary>
    public bool IsCommand => CommandText is not null;
}

/// <summary>
/// Builds <see cref="DbConnection"/> and <see cref="DbDataSource"/> veneers for any ADO.NET provider.
/// Connection opens, commands and transaction commits go through the timeline.
/// </summary>
/// <example>
/// <code>
/// var orders = new SqlFactory()
///     .After(5, TimeUnit.Seconds).For(20, TimeUnit.Seconds).Fail(SqlServerFaults.Deadlock);
///
/// DbConnection connection = orders.CreateConnection(new SqlConnection(connectionString));
/// </code>
/// </example>
public sealed class SqlFactory : ChaosFactory<SqlFactory, SqlChaosCall>
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public SqlFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public SqlFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates a <see cref="DbConnection"/> veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public DbConnection CreateConnection(DbConnection inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var veneer = new ChaosDbConnection(inner, Engine);
        Engine.Start();
        return veneer;
    }

    /// <summary>Creates a <see cref="DbDataSource"/> veneer over <paramref name="inner"/> and starts the timeline if it has not started.</summary>
    public DbDataSource CreateDataSource(DbDataSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var veneer = new ChaosDbDataSource(inner, Engine);
        Engine.Start();
        return veneer;
    }
}

/// <summary>Provider-neutral database faults for a <see cref="SqlFactory"/> window.</summary>
public static class DbFaults
{
    /// <summary>Each call fails with a transient <see cref="DbException"/> that reports a command timeout.</summary>
    public static SqlFactory CommandTimeout(this WindowBuilder<SqlFactory, SqlChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(() => new ChaosDbException("Execution Timeout Expired. The timeout period elapsed prior to completion of the operation.", new TimeoutException()));
    }

    /// <summary>Each call fails with a transient <see cref="DbException"/> that reports a broken connection.</summary>
    public static SqlFactory ConnectionFailure(this WindowBuilder<SqlFactory, SqlChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(() => new ChaosDbException("A transport-level error has occurred. The connection was closed by the remote host."));
    }
}

/// <summary>A transient <see cref="DbException"/> thrown by <see cref="DbFaults"/>.</summary>
public sealed class ChaosDbException : DbException
{
    /// <summary>Creates the exception.</summary>
    public ChaosDbException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

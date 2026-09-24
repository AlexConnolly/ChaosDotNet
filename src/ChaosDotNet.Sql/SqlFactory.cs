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

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() => [.. base.DefaultMonkeyFaults(), .. DbFaults.Catalogue(DbFaults.ConnectionFailureException, DbFaults.CommandTimeoutException)];
}

/// <summary>Provider-neutral database faults for a <see cref="SqlFactory"/> window.</summary>
public static class DbFaults
{
    /// <summary>Each call fails with a transient <see cref="DbException"/> that reports a command timeout.</summary>
    public static SqlFactory CommandTimeout(this WindowBuilder<SqlFactory, SqlChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(CommandTimeoutException);
    }

    /// <summary>Each call fails with a transient <see cref="DbException"/> that reports a broken connection.</summary>
    public static SqlFactory ConnectionFailure(this WindowBuilder<SqlFactory, SqlChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(ConnectionFailureException);
    }

    /// <summary>
    /// Each query starts normally, then the data reader throws after a few rows (0 to 4), as when the connection drops mid-read.
    /// Only <c>ExecuteReader</c> calls are affected.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="exception">The exception the reader throws. Defaults to a transient <see cref="ChaosDbException"/>.</param>
    public static SqlFactory BreakReaderMidway(this WindowBuilder<SqlFactory, SqlChaosCall> window, Func<Exception>? exception = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new ReaderFault(new Random(window.Factory.Seed), exception ?? ConnectionFailureException));
    }

    /// <summary>
    /// Builds a monkey catalogue for a database with the given exceptions: outages, timeouts, readers that break midway
    /// and random errors. Provider packages use it, for example <c>UseSqlServerFaults()</c>.
    /// </summary>
    /// <param name="connectionFailure">The exception for a lost connection.</param>
    /// <param name="timeout">The exception for a command timeout.</param>
    /// <param name="errors">Extra errors the monkey can pick at random, for example deadlocks.</param>
    public static IReadOnlyList<MonkeyFault> Catalogue(Func<Exception> connectionFailure, Func<Exception> timeout, params Func<Exception>[] errors)
    {
        ArgumentNullException.ThrowIfNull(connectionFailure);
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentNullException.ThrowIfNull(errors);
        List<MonkeyFault> catalogue =
        [
            new("ConnectionFailure", MonkeyFaultKind.Outage, _ => new FailFault(_ => connectionFailure()), 2),
            new("CommandTimeout", MonkeyFaultKind.Error, _ => new FailFault(_ => timeout())),
            new("BreakReaderMidway", MonkeyFaultKind.Weird, random => new ReaderFault(new Random(random.Next()), connectionFailure)),
        ];
        if (errors.Length > 0)
        {
            catalogue.Add(MonkeyFault.RandomException("RandomDatabaseError", MonkeyFaultKind.Error, errors));
        }

        return catalogue;
    }

    internal static Exception CommandTimeoutException() =>
        new ChaosDbException("Execution Timeout Expired. The timeout period elapsed prior to completion of the operation.", new TimeoutException());

    internal static Exception ConnectionFailureException() =>
        new ChaosDbException("A transport-level error has occurred. The connection was closed by the remote host.");
}

/// <summary>A fault that lets a query run, then makes its data reader throw after a few rows.</summary>
public sealed class ReaderFault : Fault
{
    private readonly Random _random;
    private readonly Func<Exception> _exception;

    /// <summary>Creates the fault.</summary>
    /// <param name="random">Chooses how many rows (0 to 4) each reader returns before it throws.</param>
    /// <param name="exception">Builds the exception the reader throws.</param>
    public ReaderFault(Random random, Func<Exception> exception)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <inheritdoc />
    public override string Name => "BreakReaderMidway";

    /// <inheritdoc />
    public override bool AppliesTo(ChaosCall call) => call is SqlChaosCall { Operation: "ExecuteReader" };

    /// <summary>Wraps a real data reader so that it breaks after a few rows.</summary>
    public DbDataReader Wrap(DbDataReader reader)
    {
        int rows;
        lock (_random)
        {
            rows = _random.Next(5);
        }

        return new ChaosDbDataReader(reader, rows, _exception);
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

using ChaosDotNet.Factories;
using Npgsql;

namespace ChaosDotNet;

/// <summary>
/// Real <see cref="PostgresException"/> and <see cref="NpgsqlException"/> instances for <c>Fail(...)</c>, for example
/// <c>new SqlFactory().ForCalls(2).Fail(NpgsqlFaults.Deadlock)</c>. Each property builds a new exception per call.
/// </summary>
public static class NpgsqlFaults
{
    /// <summary>SQLSTATE 40P01: deadlock detected. Transient.</summary>
    public static Func<Exception> Deadlock { get; } = () => Create("40P01", "deadlock detected");

    /// <summary>SQLSTATE 40001: could not serialize access. Transient.</summary>
    public static Func<Exception> SerializationFailure { get; } = () => Create("40001", "could not serialize access due to concurrent update");

    /// <summary>SQLSTATE 57P01: the server is shutting down. Transient.</summary>
    public static Func<Exception> AdminShutdown { get; } = () => Create("57P01", "terminating connection due to administrator command", severity: "FATAL");

    /// <summary>SQLSTATE 53300: too many connections. Transient.</summary>
    public static Func<Exception> TooManyConnections { get; } = () => Create("53300", "sorry, too many clients already", severity: "FATAL");

    /// <summary>SQLSTATE 57014: the statement was cancelled because of a statement timeout.</summary>
    public static Func<Exception> StatementTimeout { get; } = () => Create("57014", "canceling statement due to statement timeout");

    /// <summary>SQLSTATE 23505: a unique constraint was violated. Not transient.</summary>
    public static Func<Exception> UniqueViolation { get; } = () => Create("23505", "duplicate key value violates unique constraint \"orders_pkey\"");

    /// <summary>The client lost its connection to the server. Transient.</summary>
    public static Func<Exception> ConnectionLost { get; } = () =>
        new NpgsqlException("Exception while reading from stream", new EndOfStreamException("Attempted to read past the end of the stream."));

    /// <summary>The client timed out waiting for the server. Transient.</summary>
    public static Func<Exception> Timeout { get; } = () =>
        new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));

    /// <summary>
    /// Replaces the factory's monkey catalogue with one that uses real Npgsql errors: deadlocks, serialization failures,
    /// shutdowns, too many connections, statement timeouts, lost connections and readers that break midway.
    /// </summary>
    public static SqlFactory UseNpgsqlFaults(this SqlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithMonkeyFaults(
        [
            MonkeyFault.Freeze,
            MonkeyFault.Latency,
            MonkeyFault.Jitter,
            new MonkeyFault("AdminShutdown", MonkeyFaultKind.Outage, _ => new FailFault(_ => AdminShutdown())),
            new MonkeyFault("TooManyConnections", MonkeyFaultKind.Outage, _ => new FailFault(_ => TooManyConnections())),
            .. DbFaults.Catalogue(ConnectionLost, Timeout, Deadlock, SerializationFailure, StatementTimeout, UniqueViolation),
        ]);
    }

    /// <summary>Builds a <see cref="PostgresException"/>.</summary>
    /// <param name="sqlState">The five-character SQLSTATE code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="severity">The severity, for example <c>ERROR</c> or <c>FATAL</c>.</param>
    public static PostgresException Create(string sqlState, string message, string severity = "ERROR")
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        ArgumentNullException.ThrowIfNull(message);
        return new PostgresException(message, severity, severity, sqlState);
    }
}

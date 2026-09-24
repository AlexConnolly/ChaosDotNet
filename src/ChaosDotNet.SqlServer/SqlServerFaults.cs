using System.Reflection;
using ChaosDotNet.Factories;
using Microsoft.Data.SqlClient;

namespace ChaosDotNet;

/// <summary>
/// Real <see cref="SqlException"/> instances for <c>Fail(...)</c>, for example
/// <c>new SqlFactory().ForCalls(2).Fail(SqlServerFaults.Deadlock)</c>.
/// Each property builds a new exception per call. EF Core and SqlClient retry logic treat them like the real errors.
/// </summary>
public static class SqlServerFaults
{
    private const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly ConstructorInfo ErrorConstructor = typeof(SqlError).GetConstructor(
        Internal,
        [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)])
        ?? throw Unsupported("SqlError constructor");

    private static readonly ConstructorInfo CollectionConstructor = typeof(SqlErrorCollection).GetConstructor(Internal, Type.EmptyTypes)
        ?? throw Unsupported("SqlErrorCollection constructor");

    private static readonly MethodInfo CollectionAdd = typeof(SqlErrorCollection).GetMethod("Add", Internal, [typeof(SqlError)])
        ?? throw Unsupported("SqlErrorCollection.Add");

    private static readonly MethodInfo CreateExceptionMethod = typeof(SqlException).GetMethod(
        "CreateException",
        Internal,
        [typeof(SqlErrorCollection), typeof(string)])
        ?? throw Unsupported("SqlException.CreateException");

    /// <summary>Error 1205: the transaction was chosen as the deadlock victim.</summary>
    public static Func<Exception> Deadlock { get; } = () =>
        Create(1205, "Transaction (Process ID 64) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", errorClass: 13);

    /// <summary>Error -2: the command timed out.</summary>
    public static Func<Exception> Timeout { get; } = () =>
        Create(-2, "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.", errorClass: 11);

    /// <summary>Error 40613: the Azure SQL database is not available.</summary>
    public static Func<Exception> DatabaseUnavailable { get; } = () =>
        Create(40613, "Database 'orders' on server 'chaos' is not currently available.  Please retry the connection later.", errorClass: 17);

    /// <summary>Error 40501: the service is busy.</summary>
    public static Func<Exception> ServiceBusy { get; } = () =>
        Create(40501, "The service is currently busy. Retry the request after 10 seconds.", errorClass: 20);

    /// <summary>Error 10054: the server closed the connection.</summary>
    public static Func<Exception> ConnectionReset { get; } = () =>
        Create(10054, "A transport-level error has occurred when receiving results from the server. (provider: TCP Provider, error: 0 - An existing connection was forcibly closed by the remote host.)", errorClass: 20);

    /// <summary>Error 4060: the login cannot open the database.</summary>
    public static Func<Exception> CannotOpenDatabase { get; } = () =>
        Create(4060, "Cannot open database \"orders\" requested by the login. The login failed.", errorClass: 11);

    /// <summary>Error 2627: a unique constraint was violated. Not transient.</summary>
    public static Func<Exception> UniqueConstraintViolation { get; } = () =>
        Create(2627, "Violation of UNIQUE KEY constraint 'UQ_Orders'. Cannot insert duplicate key in object 'dbo.Orders'.", errorClass: 14);

    /// <summary>
    /// A transient error picked at random from the numbers EF Core and SqlClient retry on:
    /// 1205, -2, 233, 4060, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918.
    /// </summary>
    public static Func<Exception> RandomTransient { get; } = CreateRandomTransient(Random.Shared.Next());

    private static readonly int[] TransientNumbers = [1205, -2, 233, 4060, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918];

    /// <summary>
    /// Replaces the factory's monkey catalogue with one that uses real <see cref="SqlException"/> errors:
    /// deadlocks, timeouts, an unavailable database, reset connections, random transient errors and readers that break midway.
    /// </summary>
    public static SqlFactory UseSqlServerFaults(this SqlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithMonkeyFaults(
        [
            MonkeyFault.Freeze,
            MonkeyFault.Latency,
            MonkeyFault.Jitter,
            new MonkeyFault("DatabaseUnavailable", MonkeyFaultKind.Outage, _ => new FailFault(_ => DatabaseUnavailable())),
            new MonkeyFault("RandomTransient", MonkeyFaultKind.Error, random => new FailFault(_ => CreateRandomTransient(random.Next())())),
            .. DbFaults.Catalogue(ConnectionReset, Timeout, Deadlock, ServiceBusy, UniqueConstraintViolation),
        ]);
    }

    /// <summary>Builds a <see cref="SqlException"/> with one error.</summary>
    /// <param name="number">The SQL Server error number.</param>
    /// <param name="message">The error message.</param>
    /// <param name="errorClass">The severity, from 0 to 25.</param>
    /// <param name="state">The error state.</param>
    public static SqlException Create(int number, string message, byte errorClass = 16, byte state = 1)
    {
        ArgumentNullException.ThrowIfNull(message);
        var error = ErrorConstructor.Invoke([number, state, errorClass, "chaos", message, string.Empty, 0, null]);
        var errors = CollectionConstructor.Invoke(null);
        CollectionAdd.Invoke(errors, [error]);
        return (SqlException)CreateExceptionMethod.Invoke(null, [errors, "16.00.1000"])!;
    }

    private static Func<Exception> CreateRandomTransient(int seed)
    {
        var random = new Random(seed);
        return () =>
        {
            int number;
            lock (random)
            {
                number = TransientNumbers[random.Next(TransientNumbers.Length)];
            }

            return Create(number, $"A transient error occurred (error {number}). (ChaosDotNet)", errorClass: 20);
        };
    }

    private static NotSupportedException Unsupported(string member) =>
        new($"This version of Microsoft.Data.SqlClient has no {member}. Please raise an issue at https://github.com/AlexConnolly/ChaosDotNet.");
}

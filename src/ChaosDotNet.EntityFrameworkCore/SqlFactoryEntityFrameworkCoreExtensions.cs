using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChaosDotNet.Factories;

/// <summary>EF Core support for <see cref="SqlFactory"/>.</summary>
public static class SqlFactoryEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Creates an EF Core interceptor that applies the factory's timeline to connection opens, commands and commits,
    /// and starts the timeline if it has not started. Add it with <c>optionsBuilder.AddInterceptors(...)</c>.
    /// </summary>
    public static IInterceptor CreateInterceptor(this SqlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var interceptor = new ChaosDbInterceptor(factory.Engine);
        factory.Engine.Start();
        return interceptor;
    }
}

internal sealed class ChaosDbInterceptor : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private static readonly SqlChaosCall Open = new("Open");
    private static readonly SqlChaosCall Commit = new("Commit");

    private readonly ChaosEngine _engine;

    public ChaosDbInterceptor(ChaosEngine engine)
    {
        _engine = engine;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Before(Call("ExecuteReader", command));
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(Call("ExecuteReader", command), cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Before(Call("ExecuteScalar", command));
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(Call("ExecuteScalar", command), cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Before(Call("ExecuteNonQuery", command));
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(Call("ExecuteNonQuery", command), cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        _engine.CallSucceeded(Call("ExecuteReader", command));
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        _engine.CallSucceeded(Call("ExecuteReader", command));
        return new(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        _engine.CallSucceeded(Call("ExecuteScalar", command));
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        _engine.CallSucceeded(Call("ExecuteScalar", command));
        return new(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        _engine.CallSucceeded(Call("ExecuteNonQuery", command));
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        _engine.CallSucceeded(Call("ExecuteNonQuery", command));
        return new(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        _engine.CallFailed(Call(eventData.ExecuteMethod.ToString(), command), eventData.Exception);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        CommandFailed(command, eventData);
        return Task.CompletedTask;
    }

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Before(Open);
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(Open, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => _engine.CallSucceeded(Open);

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _engine.CallSucceeded(Open);
        return Task.CompletedTask;
    }

    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => _engine.CallFailed(Open, eventData.Exception);

    public Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _engine.CallFailed(Open, eventData.Exception);
        return Task.CompletedTask;
    }

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Before(Commit);
        return result;
    }

    public async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(Commit, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => _engine.CallSucceeded(Commit);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _engine.CallSucceeded(Commit);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (eventData.Action == "Commit")
        {
            _engine.CallFailed(Commit, eventData.Exception);
        }
    }

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionFailed(transaction, eventData);
        return Task.CompletedTask;
    }

    private static SqlChaosCall Call(string operation, DbCommand command) => new(operation, command.CommandText ?? string.Empty);

    private void Before(SqlChaosCall call)
    {
        if (_engine.BeforeCall(call) is { } fault)
        {
            throw Unsupported(fault);
        }
    }

    private async ValueTask BeforeAsync(SqlChaosCall call, CancellationToken cancellationToken)
    {
        if (await _engine.BeforeCallAsync(call, cancellationToken).ConfigureAwait(false) is { } fault)
        {
            throw Unsupported(fault);
        }
    }

    private static NotSupportedException Unsupported(Fault fault) =>
        new($"The fault '{fault.Name}' is not supported by the EF Core interceptor.");
}

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace ChaosDotNet.Sql;

internal sealed class ChaosDbCommand : DbCommand
{
    private readonly DbCommand _inner;
    private ChaosDbConnection? _connection;
    private ChaosDbTransaction? _transaction;

    public ChaosDbCommand(DbCommand inner, ChaosDbConnection connection)
    {
        _inner = inner;
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            switch (value)
            {
                case null:
                    _connection = null;
                    _inner.Connection = null;
                    break;
                case ChaosDbConnection chaos:
                    _connection = chaos;
                    _inner.Connection = chaos.Inner;
                    break;
                default:
                    throw new ArgumentException("A command from a ChaosDotNet connection can only use a ChaosDotNet connection.", nameof(value));
            }
        }
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            switch (value)
            {
                case null:
                    _transaction = null;
                    _inner.Transaction = null;
                    break;
                case ChaosDbTransaction chaos:
                    _transaction = chaos;
                    _inner.Transaction = chaos.Inner;
                    break;
                default:
                    throw new ArgumentException("A command from a ChaosDotNet connection can only use a ChaosDotNet transaction.", nameof(value));
            }
        }
    }

    private ChaosEngine Engine =>
        _connection?.Engine ?? throw new InvalidOperationException("The command has no connection.");

    public override void Cancel() => _inner.Cancel();

    public override void Prepare() => _inner.Prepare();

    public override Task PrepareAsync(CancellationToken cancellationToken = default) => _inner.PrepareAsync(cancellationToken);

    public override int ExecuteNonQuery() =>
        Engine.Run(SqlCalls.For("ExecuteNonQuery", _inner), _inner.ExecuteNonQuery);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        Engine.RunAsync(SqlCalls.For("ExecuteNonQuery", _inner), () => _inner.ExecuteNonQueryAsync(cancellationToken), cancellationToken);

    public override object? ExecuteScalar() =>
        Engine.Run(SqlCalls.For("ExecuteScalar", _inner), _inner.ExecuteScalar);

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        Engine.RunAsync(SqlCalls.For("ExecuteScalar", _inner), () => _inner.ExecuteScalarAsync(cancellationToken), cancellationToken);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        Engine.Run(SqlCalls.For("ExecuteReader", _inner), () => _inner.ExecuteReader(behavior));

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        Engine.RunAsync(SqlCalls.For("ExecuteReader", _inner), () => _inner.ExecuteReaderAsync(behavior, cancellationToken), cancellationToken);

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class ChaosDbTransaction : DbTransaction
{
    private readonly ChaosDbConnection _connection;

    public ChaosDbTransaction(DbTransaction inner, ChaosDbConnection connection)
    {
        Inner = inner;
        _connection = connection;
    }

    public DbTransaction Inner { get; }

    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;

    public override bool SupportsSavepoints => Inner.SupportsSavepoints;

    protected override DbConnection DbConnection => _connection;

    public override void Commit() => _connection.Engine.Run(SqlCalls.Commit, Inner.Commit);

    public override Task CommitAsync(CancellationToken cancellationToken = default) =>
        _connection.Engine.RunAsync(SqlCalls.Commit, () => Inner.CommitAsync(cancellationToken), cancellationToken);

    public override void Rollback() => _connection.Engine.Run(SqlCalls.Rollback, Inner.Rollback);

    public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _connection.Engine.RunAsync(SqlCalls.Rollback, () => Inner.RollbackAsync(cancellationToken), cancellationToken);

    public override void Save(string savepointName) => Inner.Save(savepointName);

    public override Task SaveAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.SaveAsync(savepointName, cancellationToken);

    public override void Rollback(string savepointName) => Inner.Rollback(savepointName);

    public override Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.RollbackAsync(savepointName, cancellationToken);

    public override void Release(string savepointName) => Inner.Release(savepointName);

    public override Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.ReleaseAsync(savepointName, cancellationToken);

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

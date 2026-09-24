using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ChaosDotNet.Factories;

namespace ChaosDotNet.Sql;

internal sealed class ChaosDbConnection : DbConnection
{
    public ChaosDbConnection(DbConnection inner, ChaosEngine engine)
    {
        Inner = inner;
        Engine = engine;
        Inner.StateChange += OnInnerStateChange;
    }

    public DbConnection Inner { get; }

    public ChaosEngine Engine { get; }

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        set => Inner.ConnectionString = value;
    }

    public override int ConnectionTimeout => Inner.ConnectionTimeout;

    public override string Database => Inner.Database;

    public override string DataSource => Inner.DataSource;

    public override string ServerVersion => Inner.ServerVersion;

    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

    public override Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        Inner.ChangeDatabaseAsync(databaseName, cancellationToken);

    public override void Close() => Inner.Close();

    public override Task CloseAsync() => Inner.CloseAsync();

    public override void Open() => Engine.Run(SqlCalls.Open, Inner.Open);

    public override Task OpenAsync(CancellationToken cancellationToken) =>
        Engine.RunAsync(SqlCalls.Open, () => Inner.OpenAsync(cancellationToken), cancellationToken);

    public override DataTable GetSchema() => Inner.GetSchema();

    public override DataTable GetSchema(string collectionName) => Inner.GetSchema(collectionName);

    public override DataTable GetSchema(string collectionName, string?[] restrictionValues) => Inner.GetSchema(collectionName, restrictionValues);

    public override void EnlistTransaction(System.Transactions.Transaction? transaction) => Inner.EnlistTransaction(transaction);

    public override ValueTask DisposeAsync()
    {
        Inner.StateChange -= OnInnerStateChange;
        return Inner.DisposeAsync();
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        Engine.Run(SqlCalls.BeginTransaction, () => new ChaosDbTransaction(Inner.BeginTransaction(isolationLevel), this));

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await Engine.RunAsync<DbTransaction>(
            SqlCalls.BeginTransaction,
            async () => new ChaosDbTransaction(await Inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false), this),
            cancellationToken).ConfigureAwait(false);

    protected override DbCommand CreateDbCommand() => new ChaosDbCommand(Inner.CreateCommand(), this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.StateChange -= OnInnerStateChange;
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnInnerStateChange(object sender, StateChangeEventArgs e) => OnStateChange(e);
}

internal sealed class ChaosDbDataSource : DbDataSource
{
    private readonly DbDataSource _inner;
    private readonly ChaosEngine _engine;

    public ChaosDbDataSource(DbDataSource inner, ChaosEngine engine)
    {
        _inner = inner;
        _engine = engine;
    }

    public override string ConnectionString => _inner.ConnectionString;

    protected override DbConnection CreateDbConnection() => new ChaosDbConnection(_inner.CreateConnection(), _engine);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore() => _inner.DisposeAsync();
}

using System.Data.Common;
using ChaosDotNet.Factories;

namespace ChaosDotNet.Sql;

internal static class SqlCalls
{
    public static SqlChaosCall For(string operation, DbCommand command) =>
        new(operation, command.CommandText ?? string.Empty);

    public static readonly SqlChaosCall Open = new("Open");
    public static readonly SqlChaosCall BeginTransaction = new("BeginTransaction");
    public static readonly SqlChaosCall Commit = new("Commit");
    public static readonly SqlChaosCall Rollback = new("Rollback");
}

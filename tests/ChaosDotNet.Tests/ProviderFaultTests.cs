using Microsoft.Data.SqlClient;
using Npgsql;

namespace ChaosDotNet.Tests;

public sealed class ProviderFaultTests
{
    public static TheoryData<Func<Exception>, int> SqlServerCases => new()
    {
        { SqlServerFaults.Deadlock, 1205 },
        { SqlServerFaults.Timeout, -2 },
        { SqlServerFaults.DatabaseUnavailable, 40613 },
        { SqlServerFaults.ServiceBusy, 40501 },
        { SqlServerFaults.ConnectionReset, 10054 },
        { SqlServerFaults.CannotOpenDatabase, 4060 },
        { SqlServerFaults.UniqueConstraintViolation, 2627 },
    };

    [Theory]
    [MemberData(nameof(SqlServerCases))]
    public void SqlServer_faults_build_real_sql_exceptions(Func<Exception> fault, int number)
    {
        var error = Assert.IsType<SqlException>(fault());

        Assert.Equal(number, error.Number);
        Assert.Equal(number, Assert.Single(error.Errors.Cast<SqlError>()).Number);
        Assert.NotSame(error, fault());
    }

    [Fact]
    public void SqlServer_create_sets_class_and_state()
    {
        var error = SqlServerFaults.Create(50000, "custom", errorClass: 16, state: 3);

        Assert.Equal("custom", error.Message);
        Assert.Equal(16, error.Class);
        Assert.Equal(3, error.State);
    }

    public static TheoryData<Func<Exception>, string, bool> PostgresCases => new()
    {
        { NpgsqlFaults.Deadlock, "40P01", true },
        { NpgsqlFaults.SerializationFailure, "40001", true },
        { NpgsqlFaults.AdminShutdown, "57P01", true },
        { NpgsqlFaults.TooManyConnections, "53300", true },
        { NpgsqlFaults.StatementTimeout, "57014", false },
        { NpgsqlFaults.UniqueViolation, "23505", false },
    };

    [Theory]
    [MemberData(nameof(PostgresCases))]
    public void Npgsql_faults_build_real_postgres_exceptions(Func<Exception> fault, string sqlState, bool transient)
    {
        var error = Assert.IsType<PostgresException>(fault());

        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public void Npgsql_connection_faults_are_transient()
    {
        Assert.True(Assert.IsType<NpgsqlException>(NpgsqlFaults.ConnectionLost()).IsTransient);
        Assert.True(Assert.IsType<NpgsqlException>(NpgsqlFaults.Timeout()).IsTransient);
    }
}

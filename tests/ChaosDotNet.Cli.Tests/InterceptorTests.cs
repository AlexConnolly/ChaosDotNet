using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace ChaosDotNet.Cli.Tests;

// The patches are process-wide, so every test that installs them lives in this one class and runs in order.
public sealed class InterceptorTests
{
    [Fact]
    public async Task Any_HttpClient_follows_the_timeline_then_reaches_the_network()
    {
        var http = new HttpFactory().Named("http");
        http.ForCalls(1).Respond(HttpStatusCode.ServiceUnavailable);
        Interceptors.Install(http, new SqlFactory().Named("sql"));

        using var client = new HttpClient();
        var url = $"http://127.0.0.1:{ClosedPort()}/";

        using var faulted = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, faulted.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));
        http.Verify().FaultsInjected(exactly: 1);
    }

    [Fact]
    public async Task Any_DbConnection_follows_the_timeline_then_reaches_the_database()
    {
        var sql = new SqlFactory().Named("sql");
        sql.When(call => call.IsCommand).ForCalls(2).CommandTimeout();
        Interceptors.Install(new HttpFactory().Named("http"), sql);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42";

        Assert.Throws<ChaosDbException>(() => command.ExecuteScalar());
        await Assert.ThrowsAsync<ChaosDbException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(42L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        sql.Verify().FaultsInjected(exactly: 2);
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

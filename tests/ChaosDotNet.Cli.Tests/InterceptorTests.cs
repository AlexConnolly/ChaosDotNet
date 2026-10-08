using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ChaosDotNet.Cli.Tests;

// The patches are process-wide, so every test that installs them lives in this one class and runs in order.
public sealed class InterceptorTests
{
    [Fact]
    public async Task Any_HttpClient_follows_the_timeline_then_reaches_the_network()
    {
        var http = new HttpFactory().Named("http");
        http.ForCalls(1).Respond(HttpStatusCode.ServiceUnavailable);
        Interceptors.Install(http, new SqlFactory().Named("sql"), new SocketFactory().Named("tcp"));

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
        Interceptors.Install(new HttpFactory().Named("http"), sql, new SocketFactory().Named("tcp"));

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42";

        Assert.Throws<ChaosDbException>(() => command.ExecuteScalar());
        await Assert.ThrowsAsync<ChaosDbException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(42L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        sql.Verify().FaultsInjected(exactly: 2);
    }

    [Fact]
    public async Task Any_tcp_connection_follows_the_timeline_then_reaches_the_server()
    {
        var tcp = new SocketFactory().Named("tcp");
        tcp.ForCalls(1).Fail(() => new SocketException((int)SocketError.ConnectionRefused));
        Interceptors.Install(new HttpFactory().Named("http"), new SqlFactory().Named("sql"), tcp);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = (IPEndPoint)server.LocalEndpoint;

        using (var refused = new TcpClient())
        {
            var error = await Assert.ThrowsAsync<SocketException>(() => refused.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        }

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken);
        Assert.True(client.Connected);
        tcp.Verify().FaultsInjected(exactly: 1);
    }

    [Fact]
    public void Services_mode_proxies_the_apps_interfaces_in_every_container_but_not_the_frameworks()
    {
        Interceptors.InstallServices(() => new ChaosMonkey(seed: 1));
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Greeter>();
        services.AddHttpClient();

        using var provider = services.BuildServiceProvider();

        var greeter = provider.GetRequiredService<IGreeter>();
        Assert.IsNotType<Greeter>(greeter);
        Assert.Equal("Hello", greeter.Greet());
        Assert.Equal("DefaultHttpClientFactory", provider.GetRequiredService<IHttpClientFactory>().GetType().Name);
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public interface IGreeter
    {
        string Greet();
    }

    private sealed class Greeter : IGreeter
    {
        public string Greet() => "Hello";
    }
}

using System.Net;
using System.Net.Sockets;

namespace ChaosDotNet.Tests;

public sealed class HttpFactoryTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly StubHandler _server = new();

    [Fact]
    public async Task Requests_pass_through_to_the_inner_handler()
    {
        using var client = new HttpFactory(_clock).CreateClient(new Uri("https://payments.test"), _server);

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://payments.test/health", _server.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task Respond_returns_a_fake_response_with_retry_after()
    {
        using var client = new HttpFactory(_clock)
            .ForCalls(1).Respond(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 2)
            .CreateClient(new Uri("https://payments.test"), _server);

        using var fake = await client.GetAsync("/pay", TestContext.Current.CancellationToken);
        using var real = await client.GetAsync("/pay", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, fake.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), fake.Headers.RetryAfter!.Delta);
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Respond_can_build_a_custom_response()
    {
        using var client = new HttpFactory(_clock)
            .Forever().Respond(request => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { ReasonPhrase = request.RequestUri!.AbsolutePath })
            .CreateClient(new Uri("https://payments.test"), _server);

        using var response = await client.GetAsync("/quota", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("/quota", response.ReasonPhrase);
    }

    [Fact]
    public async Task Timeout_looks_like_an_HttpClient_timeout()
    {
        using var client = new HttpFactory(_clock).Forever().Timeout().CreateClient(new Uri("https://payments.test"), _server);

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("/", TestContext.Current.CancellationToken));

        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task ConnectionRefused_looks_like_a_refused_socket()
    {
        using var client = new HttpFactory(_clock).Forever().ConnectionRefused().CreateClient(new Uri("https://payments.test"), _server);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.ConnectionError, error.HttpRequestError);
        Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(error.InnerException).SocketErrorCode);
    }

    [Fact]
    public async Task ConnectionClosed_looks_like_a_dropped_response()
    {
        using var client = new HttpFactory(_clock).Forever().ConnectionClosed().CreateClient(new Uri("https://payments.test"), _server);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.ResponseEnded, error.HttpRequestError);
    }

    [Fact]
    public async Task Freeze_holds_requests_until_the_window_ends()
    {
        using var client = new HttpFactory(_clock).For(10, TimeUnit.Seconds).Freeze().CreateClient(new Uri("https://payments.test"), _server);

        var request = client.GetAsync("/", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(request.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(5));

        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task When_can_filter_on_the_request()
    {
        using var client = new HttpFactory(_clock)
            .When(call => call.Request.RequestUri!.AbsolutePath.StartsWith("/pay", StringComparison.Ordinal))
            .Forever().Respond(HttpStatusCode.BadGateway)
            .CreateClient(new Uri("https://payments.test"), _server);

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        using var pay = await client.PostAsync("/pay", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, pay.StatusCode);
    }

    [Fact]
    public async Task CreateHandler_works_as_a_delegating_handler()
    {
        var handler = new HttpFactory(_clock).Forever().Respond(HttpStatusCode.ServiceUnavailable).CreateHandler();
        handler.InnerHandler = _server;
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("https://payments.test/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void Synchronous_send_follows_the_timeline()
    {
        using var client = new HttpFactory(_clock).ForCalls(1).Respond(HttpStatusCode.Conflict).CreateClient(new Uri("https://payments.test"), _server);

        using var fake = client.Send(new HttpRequestMessage(HttpMethod.Get, "/"), TestContext.Current.CancellationToken);
        using var real = client.Send(new HttpRequestMessage(HttpMethod.Get, "/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, fake.StatusCode);
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
    }

    [Fact]
    public async Task Real_failures_and_successes_are_logged()
    {
        var factory = new HttpFactory(_clock);
        using var client = factory.CreateClient(new Uri("https://payments.test"), _server);

        await client.GetAsync("/", TestContext.Current.CancellationToken);
        _server.Throw = new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", TestContext.Current.CancellationToken));

        Assert.Equal(1, factory.Log.Count(e => e.Kind == ChaosEventKind.CallSucceeded && e.Operation == "GET"));
        Assert.Equal(1, factory.Log.Count(e => e.Kind == ChaosEventKind.CallFailed));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }
}

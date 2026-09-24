using System.Net;
using System.Net.Sockets;

namespace ChaosDotNet.Factories;

/// <summary>A request made through an <see cref="HttpFactory"/> veneer.</summary>
public sealed class HttpChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    public HttpChaosCall(HttpRequestMessage request)
        : base(request.Method.Method)
    {
        Request = request;
    }

    /// <summary>The request being sent.</summary>
    public HttpRequestMessage Request { get; }
}

/// <summary>
/// Builds <see cref="HttpClient"/> and <see cref="DelegatingHandler"/> veneers.
/// </summary>
/// <example>
/// <code>
/// var payments = new HttpFactory()
///     .For(10, TimeUnit.Seconds).Freeze()
///     .Then().ForCalls(3).Respond(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 2);
///
/// HttpClient client = payments.CreateClient(new Uri("https://payments.test"));
/// </code>
/// </example>
public sealed class HttpFactory : ChaosFactory<HttpFactory, HttpChaosCall>
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public HttpFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public HttpFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> veneer and starts the timeline if it has not started.
    /// </summary>
    /// <param name="baseAddress">The client's base address.</param>
    /// <param name="inner">The handler that sends real requests. Defaults to a new <see cref="SocketsHttpHandler"/>.</param>
    public HttpClient CreateClient(Uri? baseAddress = null, HttpMessageHandler? inner = null)
    {
        var client = new HttpClient(new ChaosHttpHandler(Engine) { InnerHandler = inner ?? new SocketsHttpHandler() });
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }

        Engine.Start();
        return client;
    }

    /// <summary>
    /// Creates a <see cref="DelegatingHandler"/> veneer with no inner handler, for
    /// <c>services.AddHttpClient(...).AddHttpMessageHandler(() => factory.CreateHandler())</c>.
    /// Starts the timeline if it has not started.
    /// </summary>
    public DelegatingHandler CreateHandler()
    {
        var handler = new ChaosHttpHandler(Engine);
        Engine.Start();
        return handler;
    }
}

/// <summary>HTTP faults for <see cref="WindowBuilder{TSelf,TCall}"/> on an <see cref="HttpFactory"/>.</summary>
public static class HttpFaults
{
    /// <summary>Each request gets a fake response with this status code. It does not reach the real server.</summary>
    /// <param name="window">The window.</param>
    /// <param name="status">The status code.</param>
    /// <param name="retryAfterSeconds">When more than zero, adds a <c>Retry-After</c> header.</param>
    public static HttpFactory Respond(this WindowBuilder<HttpFactory, HttpChaosCall> window, HttpStatusCode status, int retryAfterSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new HttpResponseFault(request =>
        {
            var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(string.Empty) };
            if (retryAfterSeconds > 0)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            }

            return response;
        }));
    }

    /// <summary>Each request gets the response built by <paramref name="response"/>. It does not reach the real server.</summary>
    public static HttpFactory Respond(this WindowBuilder<HttpFactory, HttpChaosCall> window, Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(response);
        return window.Inject(new HttpResponseFault(response));
    }

    /// <summary>Each request fails the way <see cref="HttpClient"/> fails when its timeout expires.</summary>
    public static HttpFactory Timeout(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(() => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout elapsing.",
            new TimeoutException("The operation was canceled.")));
    }

    /// <summary>Each request fails the way it does when the server refuses the connection.</summary>
    public static HttpFactory ConnectionRefused(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(call => new HttpRequestException(
            HttpRequestError.ConnectionError,
            $"No connection could be made because the target machine actively refused it. ({call.Request.RequestUri?.Authority})",
            new SocketException((int)SocketError.ConnectionRefused)));
    }

    /// <summary>Each request fails the way it does when the server closes the connection before responding.</summary>
    public static HttpFactory ConnectionClosed(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(() => new HttpRequestException(
            HttpRequestError.ResponseEnded,
            "An error occurred while sending the request.",
            new IOException("The response ended prematurely.")));
    }
}

/// <summary>A fault that returns a fake HTTP response.</summary>
public sealed class HttpResponseFault : Fault
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

    /// <summary>Creates the fault.</summary>
    public HttpResponseFault(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        _response = response;
    }

    /// <inheritdoc />
    public override string Name => "Respond";

    /// <summary>Builds the response for a request.</summary>
    public HttpResponseMessage CreateResponse(HttpRequestMessage request) => _response(request);
}

internal sealed class ChaosHttpHandler : DelegatingHandler
{
    private readonly ChaosEngine _engine;

    public ChaosHttpHandler(ChaosEngine engine)
    {
        _engine = engine;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new HttpChaosCall(request);
        var fault = await _engine.BeforeCallAsync(call, cancellationToken).ConfigureAwait(false);
        if (fault is HttpResponseFault respond)
        {
            return respond.CreateResponse(request);
        }

        if (fault is not null)
        {
            throw new NotSupportedException($"The fault '{fault.Name}' is not supported by HttpFactory.");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return response;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new HttpChaosCall(request);
        var fault = _engine.BeforeCall(call, cancellationToken);
        if (fault is HttpResponseFault respond)
        {
            return respond.CreateResponse(request);
        }

        if (fault is not null)
        {
            throw new NotSupportedException($"The fault '{fault.Name}' is not supported by HttpFactory.");
        }

        try
        {
            var response = base.Send(request, cancellationToken);
            _engine.CallSucceeded(call);
            return response;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ChaosDotNet.Factories;

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
        return window.Inject(HttpChaos.Status(status, retryAfterSeconds));
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
        return window.Fail(HttpChaos.Timeout);
    }

    /// <summary>Each request fails the way it does when the server refuses the connection.</summary>
    public static HttpFactory ConnectionRefused(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(HttpChaos.ConnectionRefused);
    }

    /// <summary>Each request fails the way it does when the server closes the connection before responding.</summary>
    public static HttpFactory ConnectionClosed(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Fail(HttpChaos.ConnectionClosed);
    }

    /// <summary>
    /// Each request fails with a network error picked at random: connection reset, refused or closed, DNS failure,
    /// TLS failure, proxy failure or timeout.
    /// </summary>
    public static HttpFactory RandomNetworkError(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.FailRandomly(HttpChaos.NetworkErrors);
    }

    /// <summary>
    /// Each request gets a status code picked at random from ones clients rarely handle:
    /// 400, 401, 403, 404, 409, 418, 422, 429, 500, 502, 503, 504 or 507.
    /// </summary>
    public static HttpFactory RespondUnexpectedStatus(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.UnexpectedStatus(new Random(window.Factory.Seed)));
    }

    /// <summary>Each request gets <c>200 OK</c> with <c>application/json</c> content that is cut off halfway.</summary>
    public static HttpFactory RespondMalformedJson(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.MalformedJson());
    }

    /// <summary>Each request gets <c>200 OK</c> with an HTML "down for maintenance" page, as a proxy or load balancer might send.</summary>
    public static HttpFactory RespondMaintenancePage(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.MaintenancePage());
    }

    /// <summary>Each request gets <c>502 Bad Gateway</c> with an HTML page from a proxy, instead of the API's usual error body.</summary>
    public static HttpFactory RespondProxyErrorPage(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.ProxyErrorPage());
    }

    /// <summary>Each request gets <c>200 OK</c> with <c>application/json</c> but an empty body.</summary>
    public static HttpFactory RespondEmpty(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.Empty());
    }

    /// <summary>Each request gets <c>200 OK</c> with <c>application/json</c> but random bytes as the body.</summary>
    public static HttpFactory RespondGarbage(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(HttpChaos.Garbage(new Random(window.Factory.Seed)));
    }

    /// <summary>
    /// Each request reaches the real server, but the response body stops halfway and reading it throws
    /// <see cref="IOException"/>, as when a connection drops mid-download.
    /// </summary>
    public static HttpFactory BreakBodyMidway(this WindowBuilder<HttpFactory, HttpChaosCall> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Inject(new BreakBodyFault());
    }
}

/// <summary>A fault that returns a fake HTTP response.</summary>
public sealed class HttpResponseFault : Fault
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

    /// <summary>Creates the fault.</summary>
    /// <param name="response">Builds the response for a request.</param>
    /// <param name="name">The name shown in the log.</param>
    public HttpResponseFault(Func<HttpRequestMessage, HttpResponseMessage> response, string name = "Respond")
    {
        _response = response;
        Name = name;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Builds the response for a request.</summary>
    public HttpResponseMessage CreateResponse(HttpRequestMessage request) => _response(request);
}

/// <summary>A fault that lets the request reach the server, then breaks the response body halfway.</summary>
public sealed class BreakBodyFault : Fault
{
    /// <inheritdoc />
    public override string Name => "BreakBodyMidway";
}

internal static class HttpChaos
{
    public static readonly Func<Exception>[] NetworkErrors =
    [
        () => new HttpRequestException(HttpRequestError.ConnectionError, "An existing connection was forcibly closed by the remote host.", new SocketException((int)SocketError.ConnectionReset)),
        () => new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.", new SocketException((int)SocketError.ConnectionRefused)),
        () => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.", new SocketException((int)SocketError.HostNotFound)),
        () => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.", new AuthenticationException("Authentication failed because the remote party has closed the transport stream.")),
        () => new HttpRequestException(HttpRequestError.ProxyTunnelError, "The proxy tunnel request to proxy 'http://proxy:8080/' failed with status code '502'."),
        ConnectionClosed,
        Timeout,
    ];

    private static readonly HttpStatusCode[] Unexpected =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
        HttpStatusCode.Conflict, (HttpStatusCode)418, HttpStatusCode.UnprocessableEntity, HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout, HttpStatusCode.InsufficientStorage,
    ];

    public static Exception Timeout() => new TaskCanceledException(
        "The request was canceled due to the configured HttpClient.Timeout elapsing.",
        new TimeoutException("The operation was canceled."));

    public static Exception ConnectionRefused(HttpChaosCall call) => new HttpRequestException(
        HttpRequestError.ConnectionError,
        $"No connection could be made because the target machine actively refused it. ({call.Request.RequestUri?.Authority})",
        new SocketException((int)SocketError.ConnectionRefused));

    public static Exception ConnectionClosed() => new HttpRequestException(
        HttpRequestError.ResponseEnded,
        "An error occurred while sending the request.",
        new IOException("The response ended prematurely."));

    public static HttpResponseFault Status(HttpStatusCode status, int retryAfterSeconds = 0) => new(
        request =>
        {
            var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(string.Empty) };
            if (retryAfterSeconds > 0)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            }

            return response;
        },
        $"Respond({(int)status})");

    public static HttpResponseFault UnexpectedStatus(Random random) => new(
        request =>
        {
            HttpStatusCode status;
            lock (random)
            {
                status = Unexpected[random.Next(Unexpected.Length)];
            }

            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":\"unexpected\"}", Encoding.UTF8, "application/json"),
            };
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            }

            return response;
        },
        "RespondUnexpectedStatus");

    public static HttpResponseFault MalformedJson() => new(
        request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("{\"id\": 42, \"status\": \"pa", Encoding.UTF8, "application/json"),
        },
        "RespondMalformedJson");

    public static HttpResponseFault MaintenancePage() => new(
        request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("<!DOCTYPE html><html><head><title>Down for maintenance</title></head><body><h1>We'll be back soon!</h1></body></html>", Encoding.UTF8, "text/html"),
        },
        "RespondMaintenancePage");

    public static HttpResponseFault ProxyErrorPage() => new(
        request => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            RequestMessage = request,
            Content = new StringContent("<html><head><title>502 Bad Gateway</title></head><body><center><h1>502 Bad Gateway</h1></center><hr><center>nginx</center></body></html>", Encoding.UTF8, "text/html"),
        },
        "RespondProxyErrorPage");

    public static HttpResponseFault Empty() => new(
        request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = Json([]) },
        "RespondEmpty");

    public static HttpResponseFault Garbage(Random random) => new(
        request =>
        {
            var bytes = new byte[64];
            lock (random)
            {
                random.NextBytes(bytes);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = Json(bytes) };
        },
        "RespondGarbage");

    public static IEnumerable<MonkeyFault> Catalogue() =>
    [
        new("ConnectionRefused", MonkeyFaultKind.Outage, _ => new FailFault(call => ConnectionRefused((HttpChaosCall)call)), 2),
        new("Respond(503)", MonkeyFaultKind.Outage, _ => Status(HttpStatusCode.ServiceUnavailable, 2), 2),
        new("Timeout", MonkeyFaultKind.Error, _ => new FailFault(_ => Timeout())),
        MonkeyFault.RandomException("RandomNetworkError", MonkeyFaultKind.Error, NetworkErrors),
        new("RespondUnexpectedStatus", MonkeyFaultKind.Error, random => UnexpectedStatus(new Random(random.Next())), 2),
        new("RespondMalformedJson", MonkeyFaultKind.Weird, _ => MalformedJson()),
        new("RespondMaintenancePage", MonkeyFaultKind.Weird, _ => MaintenancePage()),
        new("RespondProxyErrorPage", MonkeyFaultKind.Weird, _ => ProxyErrorPage()),
        new("RespondEmpty", MonkeyFaultKind.Weird, _ => Empty()),
        new("RespondGarbage", MonkeyFaultKind.Weird, random => Garbage(new Random(random.Next()))),
        new("BreakBodyMidway", MonkeyFaultKind.Weird, _ => new BreakBodyFault()),
    ];

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}

internal sealed class BreakingStream(Stream inner, long breakAfter) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Allowed(count));
        return Count(read);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken).ConfigureAwait(false);
        return Count(read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Allowed(int count)
    {
        if (_read >= breakAfter)
        {
            throw Broken();
        }

        return (int)Math.Min(count, breakAfter - _read);
    }

    private int Count(int read)
    {
        _read += read;
        return read == 0 ? throw Broken() : read;
    }

    private static IOException Broken() => new("The response ended prematurely, with at least 1 additional bytes expected. (ChaosDotNet)");
}

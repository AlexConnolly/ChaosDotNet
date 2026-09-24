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

    /// <inheritdoc />
    public override string? Details => Request.RequestUri?.IsAbsoluteUri == true ? Request.RequestUri.PathAndQuery : Request.RequestUri?.ToString();
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

    /// <inheritdoc />
    protected override IEnumerable<MonkeyFault> DefaultMonkeyFaults() => [.. base.DefaultMonkeyFaults(), .. HttpChaos.Catalogue()];
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

        EnsureSupported(fault);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _engine.CallSucceeded(call);
            return fault is BreakBodyFault ? await BreakAsync(response, cancellationToken).ConfigureAwait(false) : response;
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

        EnsureSupported(fault);
        try
        {
            var response = base.Send(request, cancellationToken);
            _engine.CallSucceeded(call);
            return fault is BreakBodyFault ? BreakAsync(response, cancellationToken).GetAwaiter().GetResult() : response;
        }
        catch (Exception ex)
        {
            _engine.CallFailed(call, ex);
            throw;
        }
    }

    private static void EnsureSupported(Fault? fault)
    {
        if (fault is not null and not BreakBodyFault)
        {
            throw new NotSupportedException($"The fault '{fault.Name}' is not supported by HttpFactory.");
        }
    }

    private static async Task<HttpResponseMessage> BreakAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return response;
        }

        var broken = new StreamContent(new BreakingStream(new MemoryStream(body), body.Length / 2));
        foreach (var header in response.Content.Headers)
        {
            broken.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        broken.Headers.ContentLength = null;
        response.Content = broken;
        return response;
    }
}

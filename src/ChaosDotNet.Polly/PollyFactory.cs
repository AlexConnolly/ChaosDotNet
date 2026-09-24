using Polly;

namespace ChaosDotNet.Factories;

/// <summary>An execution of a Polly pipeline that holds a <see cref="PollyFactory"/> timeline.</summary>
public sealed class PollyChaosCall : ChaosCall
{
    /// <summary>Creates a call.</summary>
    public PollyChaosCall(ResilienceContext context)
        : base(context?.OperationKey ?? "Execute")
    {
        Context = context!;
    }

    /// <summary>The Polly context of the execution.</summary>
    public ResilienceContext Context { get; }
}

/// <summary>
/// Holds a chaos timeline for a Polly v8 pipeline. Add it with
/// <see cref="PollyFactoryExtensions.AddChaosTimeline{TBuilder}(TBuilder, PollyFactory)"/>, inside the strategies you want to test.
/// </summary>
/// <example>
/// <code>
/// var chaos = new PollyFactory().For(10, TimeUnit.Seconds).Fail&lt;HttpRequestException&gt;();
///
/// var pipeline = new ResiliencePipelineBuilder()
///     .AddRetry(new RetryStrategyOptions())
///     .AddChaosTimeline(chaos)
///     .Build();
/// </code>
/// </example>
public sealed class PollyFactory : ChaosFactory<PollyFactory, PollyChaosCall>
{
    /// <summary>Creates a factory with its own clock and seed.</summary>
    public PollyFactory(TimeProvider? clock = null, int? seed = null)
        : base(clock, seed)
    {
    }

    /// <summary>Creates a factory that shares the scenario's clock and start time.</summary>
    public PollyFactory(ChaosScenario scenario)
        : base(scenario)
    {
    }

    /// <summary>Creates a Polly strategy that follows the timeline, and starts the timeline if it has not started.</summary>
    public ResilienceStrategy CreateStrategy()
    {
        var strategy = new ChaosTimelineStrategy(Engine);
        Engine.Start();
        return strategy;
    }
}

/// <summary>Adds <see cref="PollyFactory"/> timelines to Polly pipelines.</summary>
public static class PollyFactoryExtensions
{
    /// <summary>Adds a strategy that follows the factory's timeline. Strategies added before it (for example a retry) see its faults.</summary>
    public static TBuilder AddChaosTimeline<TBuilder>(this TBuilder builder, PollyFactory factory)
        where TBuilder : ResiliencePipelineBuilderBase
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        return builder.AddStrategy(_ => factory.CreateStrategy(), new ChaosTimelineStrategyOptions());
    }
}

/// <summary>Options for the chaos timeline strategy.</summary>
public sealed class ChaosTimelineStrategyOptions : ResilienceStrategyOptions
{
    /// <summary>Creates the options.</summary>
    public ChaosTimelineStrategyOptions()
    {
        Name = "ChaosTimeline";
    }
}

internal sealed class ChaosTimelineStrategy : ResilienceStrategy
{
    private readonly ChaosEngine _engine;

    public ChaosTimelineStrategy(ChaosEngine engine)
    {
        _engine = engine;
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context,
        TState state)
    {
        var call = new PollyChaosCall(context);
        try
        {
            if (await _engine.BeforeCallAsync(call, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext) is { } fault)
            {
                return Outcome.FromException<TResult>(new NotSupportedException($"The fault '{fault.Name}' is not supported by PollyFactory."));
            }
        }
        catch (Exception ex)
        {
            return Outcome.FromException<TResult>(ex);
        }

        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        if (outcome.Exception is null)
        {
            _engine.CallSucceeded(call);
        }
        else
        {
            _engine.CallFailed(call, outcome.Exception);
        }

        return outcome;
    }
}

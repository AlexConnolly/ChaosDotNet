using Polly;
using Polly.Retry;

namespace ChaosDotNet.Tests;

public sealed class PollyFactoryTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task Retry_outside_the_timeline_recovers_from_a_short_window()
    {
        var chaos = new PollyFactory(_clock).ForCalls(2).Fail<ChaosTestException>();
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3, Delay = TimeSpan.Zero, ShouldHandle = new PredicateBuilder().Handle<ChaosTestException>() })
            .AddChaosTimeline(chaos)
            .Build();
        var attempts = 0;

        var result = await pipeline.ExecuteAsync(_ => new ValueTask<int>(++attempts), TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        chaos.Verify().FaultsInjected(exactly: 2).CallsSucceeded(exactly: 1);
    }

    [Fact]
    public async Task Timeline_windows_apply_by_time()
    {
        var chaos = new PollyFactory(_clock).For(10, TimeUnit.Seconds).Fail<ChaosTestException>();
        var pipeline = new ResiliencePipelineBuilder<string>().AddChaosTimeline(chaos).Build();

        await Assert.ThrowsAsync<ChaosTestException>(async () => await pipeline.ExecuteAsync(_ => new ValueTask<string>("ok"), TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("ok", await pipeline.ExecuteAsync(_ => new ValueTask<string>("ok"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Operation_is_the_polly_operation_key()
    {
        var chaos = new PollyFactory(_clock).When(call => call.Operation == "charge").Forever().Fail<ChaosTestException>();
        var pipeline = new ResiliencePipelineBuilder().AddChaosTimeline(chaos).Build();
        var charge = ResilienceContextPool.Shared.Get("charge", TestContext.Current.CancellationToken);
        var refund = ResilienceContextPool.Shared.Get("refund", TestContext.Current.CancellationToken);

        pipeline.Execute(_ => { }, refund);
        Assert.Throws<ChaosTestException>(() => pipeline.Execute(_ => { }, charge));
    }

    [Fact]
    public async Task Real_failures_are_logged()
    {
        var chaos = new PollyFactory(_clock);
        var pipeline = new ResiliencePipelineBuilder().AddChaosTimeline(chaos).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));

        Assert.Single(chaos.Log, e => e.Kind == ChaosEventKind.CallFailed);
    }
}

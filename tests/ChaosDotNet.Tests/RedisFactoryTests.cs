using StackExchange.Redis;

namespace ChaosDotNet.Tests;

public sealed class RedisFactoryTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _commands = [];
    private readonly IConnectionMultiplexer _inner;

    public RedisFactoryTests()
    {
        var database = FakeProxy.Create<IDatabase>((method, args) =>
        {
            _commands.Add(method.Name);
            return method.Name switch
            {
                nameof(IDatabase.StringGetAsync) => Task.FromResult((RedisValue)"value"),
                nameof(IDatabase.StringGet) when args[0] is RedisKey => (RedisValue)"value",
                nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
                "get_Database" => 0,
                "get_Multiplexer" => null,
                _ => throw new NotSupportedException(method.Name),
            };
        });
        var subscriber = FakeProxy.Create<ISubscriber>((method, _) =>
        {
            _commands.Add(method.Name);
            return method.Name == nameof(ISubscriber.PublishAsync) ? Task.FromResult(1L) : throw new NotSupportedException(method.Name);
        });
        _inner = FakeProxy.Create<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            nameof(IConnectionMultiplexer.GetDatabase) => database,
            nameof(IConnectionMultiplexer.GetSubscriber) => subscriber,
            "get_IsConnected" => true,
            _ => throw new NotSupportedException(method.Name),
        });
    }

    [Fact]
    public async Task Commands_pass_through()
    {
        var multiplexer = new RedisFactory(_clock).Create(_inner);

        var value = await multiplexer.GetDatabase().StringGetAsync("session:1");

        Assert.Equal("value", value.ToString());
        Assert.True(multiplexer.IsConnected);
    }

    [Fact]
    public async Task Timeout_throws_a_real_redis_timeout()
    {
        var multiplexer = new RedisFactory(_clock).ForCalls(1).Timeout().Create(_inner);
        var database = multiplexer.GetDatabase();

        var error = await Assert.ThrowsAsync<RedisTimeoutException>(() => database.StringGetAsync("session:1"));
        var value = await database.StringGetAsync("session:1");

        Assert.Equal(CommandStatus.Sent, error.Commandstatus);
        Assert.Contains("session:1", error.Message);
        Assert.Equal("value", value.ToString());
        Assert.Single(_commands);
    }

    [Fact]
    public void ConnectionFailure_throws_a_real_connection_exception_on_sync_commands()
    {
        var database = new RedisFactory(_clock).Forever().ConnectionFailure().Create(_inner).GetDatabase();

        var error = Assert.Throws<RedisConnectionException>(() => database.StringGet("k"));

        Assert.Equal(ConnectionFailureType.SocketFailure, error.FailureType);
    }

    [Fact]
    public async Task When_filters_on_the_key()
    {
        var database = new RedisFactory(_clock)
            .When(call => call.Key?.StartsWith("session:", StringComparison.Ordinal) == true)
            .Forever().Timeout()
            .Create(_inner).GetDatabase();

        Assert.True(await database.StringSetAsync("cart:1", "x"));
        await Assert.ThrowsAsync<RedisTimeoutException>(() => database.StringSetAsync("session:1", "x"));
    }

    [Fact]
    public async Task Subscriber_commands_follow_the_timeline()
    {
        var factory = new RedisFactory(_clock).For(5, TimeUnit.Seconds).ConnectionFailure();
        var subscriber = factory.Create(_inner).GetSubscriber();

        await Assert.ThrowsAsync<RedisConnectionException>(() => subscriber.PublishAsync(RedisChannel.Literal("orders"), "hi"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await subscriber.PublishAsync(RedisChannel.Literal("orders"), "hi"));
        Assert.Equal("Publish", factory.Log.Single(e => e.Kind == ChaosEventKind.FaultInjected).Operation);
    }

    [Fact]
    public void Property_reads_do_not_go_through_the_timeline()
    {
        var multiplexer = new RedisFactory(_clock).Forever().Timeout().Create(_inner);

        Assert.Equal(0, multiplexer.GetDatabase().Database);
        Assert.Same(multiplexer, multiplexer.GetDatabase().Multiplexer);
    }
}

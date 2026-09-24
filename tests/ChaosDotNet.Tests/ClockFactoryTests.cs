namespace ChaosDotNet.Tests;

public sealed class ClockFactoryTests
{
    private readonly FakeTimeProvider _real = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void JumpForward_moves_now_ahead_then_snaps_back()
    {
        var clock = new ClockFactory(_real).For(10, TimeUnit.Seconds).JumpForward(1, TimeUnit.Hours).Create();

        Assert.Equal(_real.GetUtcNow().AddHours(1), clock.GetUtcNow());
        _real.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(_real.GetUtcNow(), clock.GetUtcNow());
    }

    [Fact]
    public void JumpBackward_makes_time_go_backwards()
    {
        var clock = new ClockFactory(_real).After(5, TimeUnit.Seconds).For(10, TimeUnit.Seconds).JumpBackward(5, TimeUnit.Minutes).Create();

        var before = clock.GetUtcNow();
        _real.Advance(TimeSpan.FromSeconds(5));
        var after = clock.GetUtcNow();

        Assert.True(after < before, $"{after} should be before {before}");
    }

    [Fact]
    public void Drift_runs_time_fast_from_the_first_read()
    {
        var clock = new ClockFactory(_real).For(1, TimeUnit.Hours).Drift(2).Create();

        var start = clock.GetUtcNow();
        var startTimestamp = clock.GetTimestamp();
        _real.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(20), clock.GetUtcNow() - start);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.GetElapsedTime(startTimestamp));
    }

    [Fact]
    public void Stall_stops_time()
    {
        var clock = new ClockFactory(_real).For(1, TimeUnit.Minutes).Stall().Create();

        var now = clock.GetUtcNow();
        var timestamp = clock.GetTimestamp();
        _real.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(now, clock.GetUtcNow());
        Assert.Equal(timestamp, clock.GetTimestamp());
    }

    [Fact]
    public void DaylightSaving_shifts_the_local_offset_but_not_utc()
    {
        _real.SetLocalTimeZone(TimeZoneInfo.Utc);
        var clock = new ClockFactory(_real).Forever().DaylightSaving(1).Create();

        Assert.Equal(TimeSpan.FromHours(1), clock.LocalTimeZone.BaseUtcOffset);
        Assert.Equal(_real.GetUtcNow(), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromHours(1), clock.GetLocalNow().Offset);
    }

    [Fact]
    public async Task LateTimers_delay_timers_and_delays()
    {
        var clock = new ClockFactory(_real).Forever().LateTimers(5, TimeUnit.Seconds).Create();

        var delay = Task.Delay(TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);
        _real.Advance(TimeSpan.FromSeconds(1));
        Assert.False(delay.IsCompleted);
        _real.Advance(TimeSpan.FromSeconds(5));

        await delay;
    }

    [Fact]
    public void NonMonotonic_timestamps_can_go_backwards()
    {
        var clock = new ClockFactory(_real, seed: 1).Forever().NonMonotonic().Create();

        var timestamps = Enumerable.Range(0, 50).Select(_ =>
        {
            _real.Advance(TimeSpan.FromMilliseconds(10));
            return clock.GetTimestamp();
        }).ToList();

        Assert.Contains(timestamps.Zip(timestamps.Skip(1)), pair => pair.Second < pair.First);
    }

    [Fact]
    public void Timestamps_stay_monotonic_under_jumps()
    {
        var clock = new ClockFactory(_real).For(10, TimeUnit.Seconds).JumpBackward(1, TimeUnit.Hours).Create();

        var first = clock.GetTimestamp();
        _real.Advance(TimeSpan.FromSeconds(10));

        Assert.True(clock.GetTimestamp() > first);
    }

    [Fact]
    public void An_expiry_check_breaks_when_the_clock_jumps()
    {
        var clock = new ClockFactory(_real).After(1, TimeUnit.Seconds).For(1, TimeUnit.Minutes).JumpForward(10, TimeUnit.Minutes).Create();
        var token = new Token(clock.GetUtcNow().AddMinutes(5));

        Assert.False(token.IsExpired(clock));
        _real.Advance(TimeSpan.FromSeconds(1));
        Assert.True(token.IsExpired(clock));
    }

    [Fact]
    public void Drift_and_stall_start_again_on_each_repeat_of_a_window()
    {
        var drift = new ClockFactory(_real).Every(1, TimeUnit.Minutes).For(10, TimeUnit.Seconds).Drift(2).Create();
        var stall = new ClockFactory(_real).Every(1, TimeUnit.Minutes).For(10, TimeUnit.Seconds).Stall().Create();

        drift.GetUtcNow();
        stall.GetUtcNow();
        _real.Advance(TimeSpan.FromSeconds(60));
        var stalledAt = stall.GetUtcNow();
        drift.GetUtcNow();
        _real.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), drift.GetUtcNow() - _real.GetUtcNow());
        Assert.Equal(_real.GetUtcNow().AddSeconds(-3), stall.GetUtcNow());
        Assert.Equal(stalledAt, stall.GetUtcNow());
    }

    [Theory]
    [InlineData(13)]
    [InlineData(-13)]
    public void DaylightSaving_rejects_shifts_no_time_zone_can_have(int hours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockFactory(_real).Forever().DaylightSaving(hours));
    }

    [Fact]
    public void DaylightSaving_keeps_the_offset_inside_the_valid_range()
    {
        _real.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("edge", TimeSpan.FromHours(13), "edge", "edge"));
        var clock = new ClockFactory(_real).Forever().DaylightSaving(3).Create();

        Assert.Equal(TimeSpan.FromHours(14), clock.LocalTimeZone.BaseUtcOffset);
    }

    [Fact]
    public void The_monkey_catalogue_only_bends_time()
    {
        var faults = new ClockFactory().MonkeyFaults.Select(f => f.Name).ToList();

        Assert.Equal(["JumpForward", "JumpBackward", "Drift", "Stall", "DaylightSaving", "LateTimers"], faults);
    }

    private sealed record Token(DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(TimeProvider clock) => clock.GetUtcNow() >= ExpiresAt;
    }
}

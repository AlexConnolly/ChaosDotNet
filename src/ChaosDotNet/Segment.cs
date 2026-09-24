namespace ChaosDotNet;

internal enum SegmentKind
{
    Gap,
    Window,
    Repeating,
}

internal enum WindowEnd
{
    Duration,
    Calls,
    Until,
    Forever,
}

internal sealed class Segment
{
    public SegmentKind Kind { get; init; }

    public WindowEnd End { get; init; }

    public TimeSpan Duration { get; init; }

    public TimeSpan Period { get; init; }

    public int Calls { get; init; }

    public Func<bool>? Until { get; init; }

    public Func<ChaosCall, bool>? When { get; init; }

    public Fault? Fault { get; set; }

    public double Rate { get; set; } = 1.0;

    public Random? Random { get; init; }

    public DateTimeOffset? LastActivation { get; set; }

    public int WindowIndex { get; set; } = -1;

    public bool IsBounded => Kind == SegmentKind.Repeating || End == WindowEnd.Duration;

    public static Segment Gap(TimeSpan duration) => new() { Kind = SegmentKind.Gap, End = WindowEnd.Duration, Duration = duration };
}

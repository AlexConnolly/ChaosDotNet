namespace ChaosDotNet;

/// <summary>The unit of an amount of time in a chaos timeline.</summary>
public enum TimeUnit
{
    /// <summary>Milliseconds.</summary>
    Milliseconds,

    /// <summary>Seconds.</summary>
    Seconds,

    /// <summary>Minutes.</summary>
    Minutes,

    /// <summary>Hours.</summary>
    Hours,
}

internal static class TimeUnitExtensions
{
    public static TimeSpan ToTimeSpan(this TimeUnit unit, double amount, string paramName = "amount")
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, amount, "The amount of time must be zero or more.");
        }

        return unit switch
        {
            TimeUnit.Milliseconds => TimeSpan.FromMilliseconds(amount),
            TimeUnit.Seconds => TimeSpan.FromSeconds(amount),
            TimeUnit.Minutes => TimeSpan.FromMinutes(amount),
            TimeUnit.Hours => TimeSpan.FromHours(amount),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown time unit."),
        };
    }
}

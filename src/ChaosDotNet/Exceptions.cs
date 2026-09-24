namespace ChaosDotNet;

/// <summary>Thrown when an unbounded freeze (<c>ForCalls</c>, <c>Until</c> or <c>Forever</c>) lasts longer than the factory's maximum freeze.</summary>
public sealed class ChaosFreezeTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    public ChaosFreezeTimeoutException(TimeSpan maxFreeze)
        : base($"The call was frozen for the maximum of {maxFreeze}. Pass a CancellationToken to the call, or change the limit with WithMaxFreeze(...).")
    {
        MaxFreeze = maxFreeze;
    }

    /// <summary>The limit that was reached.</summary>
    public TimeSpan MaxFreeze { get; }
}

/// <summary>Thrown by <c>Verify()</c> checks that fail.</summary>
public sealed class ChaosAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChaosAssertionException(string message)
        : base(message)
    {
    }
}

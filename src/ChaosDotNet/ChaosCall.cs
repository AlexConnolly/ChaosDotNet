namespace ChaosDotNet;

/// <summary>
/// One call made through a veneer. Each factory passes its own subtype (for example
/// <c>HttpChaosCall</c>) so that <c>When(...)</c> filters can inspect the call.
/// </summary>
public abstract class ChaosCall
{
    /// <summary>Creates a call.</summary>
    /// <param name="operation">A short name for what the call does, for example <c>GET</c> or <c>ExecuteReader</c>.</param>
    protected ChaosCall(string operation)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    /// <summary>A short name for what the call does, for example <c>GET</c> or <c>ExecuteReader</c>.</summary>
    public string Operation { get; }

    /// <summary>
    /// A short description of the call for logs and reports, for example the SQL text or the HTTP path.
    /// <see langword="null"/> when there is nothing more to say than <see cref="Operation"/>.
    /// </summary>
    public virtual string? Details => null;

    /// <inheritdoc />
    public override string ToString() => Operation;
}

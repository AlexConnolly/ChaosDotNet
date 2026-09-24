namespace ChaosDotNet.Tests;

public interface IInventory
{
    int Count(string sku);

    Task<int> CountAsync(string sku, CancellationToken cancellationToken = default);

    Task ReserveAsync(string sku, CancellationToken cancellationToken = default);

    ValueTask<int> PeekAsync(string sku);

    ValueTask ReleaseAsync(string sku);
}

public sealed class Inventory : IInventory
{
    public int Calls { get; private set; }

    public Exception? Throw { get; set; }

    public int Count(string sku) => Hit();

    public Task<int> CountAsync(string sku, CancellationToken cancellationToken = default) => Task.FromResult(Hit());

    public async Task ReserveAsync(string sku, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Hit();
    }

    public ValueTask<int> PeekAsync(string sku) => new(Hit());

    public ValueTask ReleaseAsync(string sku)
    {
        Hit();
        return ValueTask.CompletedTask;
    }

    private int Hit()
    {
        Calls++;
        if (Throw is not null)
        {
            throw Throw;
        }

        return 42;
    }
}

public sealed class ChaosTestException : Exception
{
    public ChaosTestException()
        : base("chaos")
    {
    }
}

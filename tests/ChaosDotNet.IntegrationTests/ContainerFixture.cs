using DotNet.Testcontainers.Containers;

namespace ChaosDotNet.IntegrationTests;

/// <summary>
/// Starts one container for a test class. Without Docker the tests skip locally, but fail on CI.
/// </summary>
public abstract class ContainerFixture<TContainer> : IAsyncLifetime
    where TContainer : IContainer
{
    private string? _unavailable;

    public TContainer Container { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            Container = Build();
            await Container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("CI") != "true" && IsDockerMissing(ex))
        {
            _unavailable = $"Docker is not available: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null && Container is not null)
        {
            await Container.DisposeAsync();
        }
    }

    public void SkipWithoutDocker()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }
    }

    protected abstract TContainer Build();

    private static bool IsDockerMissing(Exception ex) =>
        ex.GetType().Name == "DockerUnavailableException" || ex.InnerException is not null && IsDockerMissing(ex.InnerException);
}

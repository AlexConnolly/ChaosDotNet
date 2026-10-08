using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using ChaosDotNet.Cli;

/// <summary>
/// Loaded into each test process through <c>DOTNET_STARTUP_HOOKS</c> by <c>dotnet chaos test</c>. Loads the tool into its own
/// <see cref="AssemblyLoadContext"/>, so its ChaosDotNet and Harmony never clash with versions the app or tests reference,
/// then starts <see cref="Agent"/> there. Framework types such as <c>HttpMessageHandler</c> and <c>DbCommand</c> are shared,
/// so the patches still reach the app's calls.
/// </summary>
internal static class StartupHook
{
    // Processes that build or orchestrate tests rather than run them.
    private static readonly string[] Tooling = ["dotnet", "MSBuild", "vstest.console", "datacollector", "VBCSCompiler", "dotnet-chaos"];

    public static void Initialize()
    {
        var seed = Environment.GetEnvironmentVariable(Runner.SeedVariable);
        var process = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
        if (seed is null || Tooling.Contains(process, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var tool = new ToolLoadContext(Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!);
        tool.LoadFromAssemblyPath(typeof(StartupHook).Assembly.Location)
            .GetType(typeof(Agent).FullName!, throwOnError: true)!
            .GetMethod(nameof(Agent.Start), BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [int.Parse(seed, CultureInfo.InvariantCulture)]);
    }

    /// <summary>
    /// ChaosDotNet and Harmony always load from the tool's folder. Other libraries, such as Microsoft.Extensions.DependencyInjection,
    /// come from the app when it has a compatible version, so the tool sees the app's own types (its <c>IServiceCollection</c>),
    /// and from the tool's folder otherwise.
    /// </summary>
    private sealed class ToolLoadContext(string folder) : AssemblyLoadContext("ChaosDotNet.Cli")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            if (!File.Exists(path))
            {
                return null;
            }

            if (!name.Name!.StartsWith("ChaosDotNet", StringComparison.Ordinal) && name.Name != "0Harmony")
            {
                try
                {
                    return Default.LoadFromAssemblyName(name);
                }
                catch (IOException)
                {
                }
            }

            return LoadFromAssemblyPath(path);
        }
    }
}

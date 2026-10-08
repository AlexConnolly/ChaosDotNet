using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ChaosDotNet.DependencyInjection;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;

namespace ChaosDotNet.Cli;

internal static partial class Interceptors
{
    private static readonly string[] Framework = ["Microsoft.", "System."];
    private static readonly ConditionalWeakTable<IServiceCollection, object> Wrapped = [];
    private static Func<ChaosMonkey>? s_newMonkey;

    /// <summary>The monkey of each service container, with the engines it breaks, for the chaos log.</summary>
    public static ConcurrentBag<(ChaosMonkey Monkey, List<ChaosEngine> Engines)> ServiceMonkeys { get; } = [];

    /// <summary>
    /// Patches <c>BuildServiceProvider</c> so every container the app builds wraps its own interfaces in proxies that a new
    /// monkey from <paramref name="newMonkey"/> breaks. Framework services are left alone.
    /// </summary>
    public static void InstallServices(Func<ChaosMonkey> newMonkey)
    {
        lock (Patched)
        {
            s_newMonkey = newMonkey;
            if (!Patched.Add(typeof(ServiceCollectionContainerBuilderExtensions)))
            {
                return;
            }

            var target = typeof(ServiceCollectionContainerBuilderExtensions).GetMethod(
                nameof(ServiceCollectionContainerBuilderExtensions.BuildServiceProvider),
                [typeof(IServiceCollection), typeof(ServiceProviderOptions)])!;
            Harmony.Patch(target, prefix: new HarmonyMethod(typeof(Interceptors).GetMethod(nameof(BuildServiceProvider), BindingFlags.Static | BindingFlags.NonPublic)));
        }
    }

    private static void BuildServiceProvider(IServiceCollection __0)
    {
        if (s_newMonkey is not { } newMonkey || Wrapped.TryGetValue(__0, out _))
        {
            return;
        }

        Wrapped.Add(__0, new object());
        try
        {
            var types = __0
                .Where(d => !d.IsKeyedService && d.ServiceType is { IsInterface: true, IsVisible: true, IsGenericTypeDefinition: false })
                .Select(d => d.ServiceType)
                .Where(t => !Framework.Any(f => t.FullName!.StartsWith(f, StringComparison.Ordinal)))
                .Distinct()
                .GroupBy(t => t.Name) // The monkey names each dependency after its interface, and names must be unique.
                .Select(g => g.First())
                .ToList();
            if (types.Count == 0)
            {
                return;
            }

            var monkey = newMonkey();
            var engines = new List<ChaosEngine>();
            var wrap = typeof(Interceptors).GetMethod(nameof(WrapService), BindingFlags.Static | BindingFlags.NonPublic)!;
            __0.AddChaos(monkey, chaos => types.ForEach(t => wrap.MakeGenericMethod(t).Invoke(null, [chaos, engines])));
            monkey.Start();
            ServiceMonkeys.Add((monkey, engines));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet-chaos: could not add chaos to a service container: {ex.Message}");
        }
    }

    private static void WrapService<T>(ChaosServicesBuilder chaos, List<ChaosEngine> engines)
        where T : class => chaos.Interface<T>(factory => engines.Add(factory.Engine));
}

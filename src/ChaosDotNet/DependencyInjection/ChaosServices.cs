using ChaosDotNet.Factories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Adds chaos to clients already registered in a service collection.</summary>
public static class ChaosServiceCollectionExtensions
{
    /// <summary>
    /// Wraps registered clients with factories that join <paramref name="monkey"/>, so the monkey can break them at random.
    /// Call it after the app's own registrations, for example in <c>ConfigureTestServices</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddChaosMonkey(monkey, chaos => chaos.Http().EntityFrameworkCore().Clock().Interface&lt;IPaymentGateway&gt;());
    /// </code>
    /// </example>
    public static IServiceCollection AddChaosMonkey(this IServiceCollection services, ChaosMonkey monkey, Action<ChaosServicesBuilder> configure) =>
        services.AddChaos(monkey, configure);

    /// <summary>
    /// Wraps registered clients with factories that join <paramref name="scenario"/>. With a plain scenario, give each client
    /// its timeline in the configure callbacks, for example <c>chaos.Http((name, f) => f.ForCalls(3).Respond(...))</c>.
    /// </summary>
    public static IServiceCollection AddChaos(this IServiceCollection services, ChaosScenario scenario, Action<ChaosServicesBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ChaosServicesBuilder(services, scenario);
        configure(builder);
        builder.Apply();
        return services;
    }
}

/// <summary>Chooses which registered clients <c>AddChaosMonkey</c> wraps. Client packages add their own methods, such as <c>Redis()</c>.</summary>
public sealed class ChaosServicesBuilder
{
    private readonly List<Action> _steps = [];
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _wrapped = [];

    internal ChaosServicesBuilder(IServiceCollection services, ChaosScenario scenario)
    {
        Services = services;
        Scenario = scenario;
    }

    /// <summary>The service collection being changed.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The scenario or monkey the factories join.</summary>
    public ChaosScenario Scenario { get; }

    /// <summary>The dependency names wrapped so far, for example <c>http:payments</c>.</summary>
    public IReadOnlyList<string> Wrapped => _wrapped;

    /// <summary>Leaves the named dependencies alone, for example <c>Except("http:health")</c>.</summary>
    public ChaosServicesBuilder Except(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _excluded.UnionWith(names);
        return this;
    }

    /// <summary>
    /// Adds a step that runs after the configure callback, once every exclusion is known. Client packages use it to add
    /// their own wrapping, for example <c>Redis()</c>.
    /// </summary>
    public ChaosServicesBuilder Add(Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
        return this;
    }

    /// <summary>Whether a dependency name was excluded with <see cref="Except"/>.</summary>
    public bool IsExcluded(string name) => _excluded.Contains(name);

    /// <summary>Records that a dependency was wrapped.</summary>
    public void Track(string name) => _wrapped.Add(name);

    /// <summary>
    /// Replaces every non-keyed registration of <typeparamref name="TService"/> with one that wraps the original instance.
    /// Lifetimes are kept. Throws when nothing is registered, so wiring mistakes are visible.
    /// </summary>
    public void Decorate<TService>(Func<IServiceProvider, TService, TService> wrap)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(wrap);
        var found = false;
        for (var i = 0; i < Services.Count; i++)
        {
            var descriptor = Services[i];
            if (descriptor.ServiceType != typeof(TService) || descriptor.IsKeyedService)
            {
                continue;
            }

            found = true;
            Services[i] = ServiceDescriptor.Describe(
                typeof(TService),
                provider => wrap(provider, (TService)Original(provider, descriptor)),
                descriptor.Lifetime);
        }

        if (!found)
        {
            throw new InvalidOperationException($"No {typeof(TService).Name} is registered, so there is nothing to wrap. Call AddChaosMonkey after the app registers its services.");
        }
    }

    /// <summary>
    /// Wraps every named and typed <see cref="HttpClient"/> from <c>IHttpClientFactory</c>. Each client gets its own
    /// <see cref="HttpFactory"/>, named <c>http:{client name}</c>, as its innermost handler, so resilience handlers see the faults.
    /// </summary>
    /// <param name="configure">Gives each client's factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public ChaosServicesBuilder Http(Action<string, HttpFactory>? configure = null) => Add(() =>
    {
        var names = Services
            .Where(d => d.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>))
            .Select(d => d.ImplementationInstance)
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Select(o => o.Name ?? Options.DefaultName)
            .Concat(TypedClientNames())
            .Distinct()
            .ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                "No configured HttpClient was found, so there is nothing to wrap. Named clients are found once they have any configuration (for example a base address or a handler); typed clients are always found.");
        }

        foreach (var clientName in names)
        {
            var name = $"http:{(clientName.Length == 0 ? "default" : clientName)}";
            if (IsExcluded(name))
            {
                continue;
            }

            var factory = new HttpFactory(Scenario).Named(name);
            configure?.Invoke(clientName, factory);
            Services.Configure<HttpClientFactoryOptions>(clientName, options =>
                options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.AdditionalHandlers.Add(factory.CreateHandler())));
            Track(name);
        }
    });

    /// <summary>
    /// Replaces the app's <see cref="TimeProvider"/> with a <see cref="ClockFactory"/> veneer over the scenario's clock, named
    /// <c>clock</c>. Registers one if the app has none.
    /// </summary>
    /// <param name="configure">Gives the clock a hand-written timeline. Leave it out to let the monkey decide.</param>
    public ChaosServicesBuilder Clock(Action<ClockFactory>? configure = null) => Add(() =>
    {
        if (IsExcluded("clock"))
        {
            return;
        }

        var factory = new ClockFactory(Scenario).Named("clock");
        configure?.Invoke(factory);
        for (var i = Services.Count - 1; i >= 0; i--)
        {
            if (Services[i].ServiceType == typeof(TimeProvider) && !Services[i].IsKeyedService)
            {
                Services.RemoveAt(i);
            }
        }

        Services.AddSingleton(_ => factory.Create());
        Track("clock");
    });

    /// <summary>Wraps every registration of the interface <typeparamref name="T"/> with a <see cref="ProxyFactory{T}"/>, named after the interface.</summary>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public ChaosServicesBuilder Interface<T>(Action<ProxyFactory<T>>? configure = null)
        where T : class => Add(() =>
    {
        var name = typeof(T).Name;
        if (IsExcluded(name))
        {
            return;
        }

        var factory = new ProxyFactory<T>(Scenario).Named(name);
        configure?.Invoke(factory);
        Decorate<T>((_, inner) => factory.Create(inner));
        Track(name);
    });

    /// <summary>Typed client names, from the registry Microsoft.Extensions.Http keeps. Empty if the registry cannot be read.</summary>
    private IEnumerable<string> TypedClientNames()
    {
        var registry = Services.FirstOrDefault(d => d.ServiceType.Name == "HttpClientMappingRegistry")?.ImplementationInstance;
        var property = registry?.GetType().GetProperty("NamedClientRegistrations", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return property?.GetValue(registry) is System.Collections.IDictionary names ? names.Keys.OfType<string>().ToList() : [];
    }

    internal void Apply()
    {
        foreach (var step in _steps)
        {
            step();
        }
    }

    private static object Original(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}

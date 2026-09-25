using Azure.Messaging.ServiceBus;
using ChaosDotNet.Factories;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Azure Service Bus support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesServiceBusExtensions
{
    /// <summary>Wraps the registered <see cref="ServiceBusClient"/> with a <see cref="ServiceBusFactory"/> named <c>servicebus</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder ServiceBus(this ChaosServicesBuilder builder, Action<ServiceBusFactory>? configure = null) =>
        builder.ServiceBus(ChaosStrategy.Proxy, null, configure);

    /// <summary>Adds chaos to <see cref="ServiceBusClient"/> with a <see cref="ServiceBusFactory"/> named <c>servicebus</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="strategy">
    /// <see cref="ChaosStrategy.Proxy"/> wraps the app's client. <see cref="ChaosStrategy.Replace"/> removes it and creates a
    /// new <see cref="ServiceBusClient"/> from <paramref name="connectionString"/>.
    /// </param>
    /// <param name="connectionString">The Service Bus connection string. Required for <see cref="ChaosStrategy.Replace"/>.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder ServiceBus(this ChaosServicesBuilder builder, ChaosStrategy strategy, string? connectionString = null, Action<ServiceBusFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (strategy == ChaosStrategy.Replace)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        }

        return builder.Add(() =>
        {
            if (builder.IsExcluded("servicebus"))
            {
                return;
            }

            var factory = new ServiceBusFactory(builder.Scenario).Named("servicebus");
            configure?.Invoke(factory);
            if (strategy == ChaosStrategy.Replace)
            {
                builder.Replace<ServiceBusClient>(_ => factory.CreateClient(new ServiceBusClient(connectionString)));
            }
            else
            {
                builder.Decorate<ServiceBusClient>((_, inner) => factory.CreateClient(inner));
            }

            builder.Track("servicebus");
        });
    }
}

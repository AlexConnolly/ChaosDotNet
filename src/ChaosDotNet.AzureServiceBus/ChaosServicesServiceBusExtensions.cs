using Azure.Messaging.ServiceBus;
using ChaosDotNet.Factories;

namespace ChaosDotNet.DependencyInjection;

/// <summary>Azure Service Bus support for <c>AddChaosMonkey</c>.</summary>
public static class ChaosServicesServiceBusExtensions
{
    /// <summary>Wraps the registered <see cref="ServiceBusClient"/> with a <see cref="ServiceBusFactory"/> named <c>servicebus</c>.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Gives the factory a hand-written timeline. Leave it out to let the monkey decide.</param>
    public static ChaosServicesBuilder ServiceBus(this ChaosServicesBuilder builder, Action<ServiceBusFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(() =>
        {
            if (builder.IsExcluded("servicebus"))
            {
                return;
            }

            var factory = new ServiceBusFactory(builder.Scenario).Named("servicebus");
            configure?.Invoke(factory);
            builder.Decorate<ServiceBusClient>((_, inner) => factory.CreateClient(inner));
            builder.Track("servicebus");
        });
    }
}

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketNow.Contracts.Booking;

namespace TicketNow.BuildingBlocks.Messaging;

public static class MessagingExtensions
{
    public static IServiceCollection AddTicketNowMessaging(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName,
        Action<IBusRegistrationConfigurator>? consumers = null)
    {
        services.AddMassTransit(x =>
        {
            // Prefix queue names with the service name so every service subscribing to an event gets its own queue
            x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(serviceName, false));
            consumers?.Invoke(x);

            x.UsingRabbitMq((ctx, cfg) =>
            {
                var uri = config["RabbitMq:Uri"];
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    cfg.Host(new Uri(uri));
                }
                else
                {
                    cfg.Host(config["RabbitMq:Host"] ?? "localhost", "/", h =>
                    {
                        h.Username(config["RabbitMq:Username"] ?? "guest");
                        h.Password(config["RabbitMq:Password"] ?? "guest");
                    });
                }

                // Transient errors are retried 3 times; after that the message goes to the <queue>_error queue
                cfg.UseMessageRetry(r => r.Intervals(200, 1000, 5000));
                cfg.ConfigureEndpoints(ctx);
            });
        });

        services.AddSingleton(new OutboxTypeRegistry(typeof(OrderPlaced).Assembly));
        return services;
    }

    public static IServiceCollection AddOutboxPublisher<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
        => services.AddHostedService<OutboxPublisher<TDbContext>>();
}
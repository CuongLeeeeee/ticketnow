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
            // Tiền tố tên service cho queue: xem giải thích bên dưới
            x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(serviceName, false));
            consumers?.Invoke(x);

            x.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host(config["RabbitMq:Host"] ?? "localhost", "/", h =>
                {
                    h.Username(config["RabbitMq:Username"] ?? "guest");
                    h.Password(config["RabbitMq:Password"] ?? "guest");
                });

                // Lỗi tạm thời => thử lại 3 lần; vẫn lỗi => message vào queue <tên>_error
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
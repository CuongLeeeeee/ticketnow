using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TicketNow.BuildingBlocks.Messaging;

namespace TicketNow.BuildingBlocks.Observability;

public static class TelemetryExtensions
{
    public static IServiceCollection AddTicketNowTelemetry(
        this IServiceCollection services, IConfiguration config, string serviceName, bool withRedis = false)
    {
        var endpoint = new Uri(config["Otlp:Endpoint"] ?? "http://localhost:4317");

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: "1.0.0"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"));
                t.AddHttpClientInstrumentation();
                t.AddNpgsql();
                t.AddSource("MassTransit");
                t.AddSource(OutboxActivity.SourceName);
                if (withRedis) t.AddRedisInstrumentation();
                t.AddOtlpExporter(o => o.Endpoint = endpoint);
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddRuntimeInstrumentation();
                m.AddOtlpExporter(o => o.Endpoint = endpoint);
            });

        return services;
    }
}
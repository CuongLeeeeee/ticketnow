using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text.Json;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Notification.Api.Hubs;
using TicketNow.Notification.Api.Services;
using TicketNow.BuildingBlocks.Observability;

var builder = WebApplication.CreateBuilder(args);

var redisConn = builder.Configuration.GetConnectionString("Redis")!;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddTicketNowTelemetry(builder.Configuration, "notification", withRedis: true);
builder.Services.AddTicketNowJwt(builder.Configuration, o =>
{
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            // WebSockets do not support the Authorization header => pass the token via the query string.
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = token;
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddSingleton<IUserIdProvider, SubUserIdProvider>();

// Backplane: With multiple Notification instances, any instance can push notifications to a client
// connected to a different instance, thanks to Redis Pub/Sub.
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConn, o =>
        o.Configuration.ChannelPrefix = RedisChannel.Literal("tn-signalr"));

builder.Services.AddTicketNowMessaging(builder.Configuration, "notification",
    x => x.AddConsumers(typeof(Program).Assembly));
builder.Services.AddHealthChecks();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHub<NotificationHub>("/hubs/notifications");

app.MapGet("/api/notifications/me", async (ClaimsPrincipal principal, IConnectionMultiplexer redis) =>
{
    var userId = principal.FindFirstValue("sub")!;
    var items = await redis.GetDatabase().ListRangeAsync($"notif:inbox:{userId}", 0, 49);
    return Results.Ok(items.Select(v => JsonSerializer.Deserialize<NotificationPayload>(v.ToString())));
}).RequireAuthorization("authenticated");

app.Run();
using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Api.BackgroundServices;
using TicketNow.Booking.Api.Endpoints;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.BuildingBlocks.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BookingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddHostedService<InventoryRebuilder>();
builder.Services.AddTicketNowMessaging(builder.Configuration, "booking",
    x => x.AddConsumers(typeof(Program).Assembly));
builder.Services.AddOutboxPublisher<BookingDbContext>();
builder.Services.AddHostedService<OrderExpirySweeper>();
builder.Services.AddTicketNowJwt(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<WaitingRoomStore>();
builder.Services.AddHostedService<WaitingRoomAdmitter>();
builder.Services.AddTicketNowTelemetry(builder.Configuration, "booking", withRedis: true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database.MigrateAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

app.MapOrderEndpoints();
app.MapWaitingRoomEndpoints();
app.MapInventoryEndpoints();

app.Run();

public partial class Program;

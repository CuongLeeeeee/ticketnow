using Microsoft.EntityFrameworkCore;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.BuildingBlocks.Observability;
using TicketNow.Catalog.Api.Endpoints;
using TicketNow.Catalog.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTicketNowTelemetry(builder.Configuration, "catalog", withRedis: true);
builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());

builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = builder.Configuration.GetConnectionString("Redis");
    o.InstanceName = "tn:catalog:";
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddTicketNowJwt(builder.Configuration);
builder.Services.AddTicketNowMessaging(builder.Configuration, "catalog");
builder.Services.AddOutboxPublisher<CatalogDbContext>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

app.MapEventEndpoints();
app.MapAdminEventEndpoints();

app.Run();

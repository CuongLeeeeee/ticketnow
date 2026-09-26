using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Text;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.Contracts.Catalog;

namespace TicketNow.IntegrationTests;

public sealed class BookingAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string SigningKey = "integration-test-signing-key-at-least-32-bytes!!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-alpine").Build();

    public async Task InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(),
                           _redis.DisposeAsync().AsTask(),
                           _rabbit.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("RabbitMq:Uri", _rabbit.GetConnectionString());
        builder.UseSetting("Jwt:Issuer", "ticketnow-identity");
        builder.UseSetting("Jwt:Audience", "ticketnow-api");
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Otlp:Endpoint", "http://localhost:59999");   // No collector; exporter silently skips it.
    }

    public HttpClient CreateClientFor(Guid userId, string role = "User")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "ticketnow-identity",
            Audience = "ticketnow-api",
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["email"] = $"{userId}@test.local",
                ["name"] = "Test",
                ["role"] = role
            }
        };

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", new JsonWebTokenHandler().CreateToken(descriptor));
        return client;
    }

    /// <summary>Publishes TicketTypePublished and waits for the consumer to build the inventory.</summary>
    public async Task<Guid> SeedTicketTypeAsync(int quantity, int maxPerUser)
    {
        var ticketTypeId = Guid.NewGuid();

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBus>().Publish(new TicketTypePublished(
            ticketTypeId, Guid.NewGuid(), "VIP", 100_000m, "VND",
            quantity, maxPerUser, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var store = Services.GetRequiredService<InventoryStore>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await store.GetAvailableAsync(ticketTypeId) == quantity) return ticketTypeId;
            await Task.Delay(200);
        }

        throw new TimeoutException("Consumer did not build the inventory within 30 seconds");
    }
}
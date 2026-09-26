using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using System.Net;
using System.Net.Http.Json;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;
using Xunit.Abstractions;

namespace TicketNow.IntegrationTests;

public sealed class BookingTests(BookingAppFactory factory, ITestOutputHelper output)
    : IClassFixture<BookingAppFactory>
{
    private static HttpRequestMessage PlaceOrder(Guid ticketTypeId, int quantity, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new
            {
                items = new[] { new { ticketTypeId, quantity } }
            })
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    [Fact(DisplayName = "200 concurrent requests for 50 tickets: exactly 50 sold, no oversell")]
    public async Task FlashSale_NeverOversells()
    {
        const int capacity = 50;
        const int attempts = 200;

        var ticketTypeId = await factory.SeedTicketTypeAsync(capacity, maxPerUser: 1);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            var client = factory.CreateClientFor(Guid.NewGuid());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.SendAsync(PlaceOrder(ticketTypeId, 1, Guid.NewGuid().ToString()));
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            return response;
        }));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        var sorted = latencies.OrderBy(x => x).ToArray();
        var p95 = sorted[(int)(sorted.Length * 0.95)];
        var median = sorted[sorted.Length / 2];

        output.WriteLine($"{attempts} concurrent requests / {capacity} tickets");
        output.WriteLine($"  Sold: {created}   Rejected (sold out): {rejected}");
        output.WriteLine($"  Latency: median {median:F0} ms, p95 {p95:F0} ms");

        created.Should().Be(capacity);
        rejected.Should().Be(attempts - capacity);

        var store = factory.Services.GetRequiredService<InventoryStore>();
        (await store.GetAvailableAsync(ticketTypeId)).Should().Be(0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var held = await db.OrderItems
            .Where(i => i.TicketTypeId == ticketTypeId && i.Order.Status == OrderStatus.AwaitingPayment)
            .SumAsync(i => i.Quantity);
        held.Should().Be(capacity);
    }

    [Fact(DisplayName = "Resending the same Idempotency-Key returns the original order")]
    public async Task SameIdempotencyKey_ReturnsSameOrder()
    {
        var ticketTypeId = await factory.SeedTicketTypeAsync(10, maxPerUser: 2);
        var client = factory.CreateClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(PlaceOrder(ticketTypeId, 1, key));
        var second = await client.SendAsync(PlaceOrder(ticketTypeId, 1, key));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replayed").Should().Contain("true");

        var a = await first.Content.ReadFromJsonAsync<OrderResponse>();
        var b = await second.Content.ReadFromJsonAsync<OrderResponse>();
        b!.Id.Should().Be(a!.Id);

        // Key point: tickets are deducted only ONCE
        var store = factory.Services.GetRequiredService<InventoryStore>();
        (await store.GetAvailableAsync(ticketTypeId)).Should().Be(9);
    }

    [Fact(DisplayName = "Exceeding the per-user ticket limit is rejected")]
    public async Task ExceedingPerUserLimit_IsRejected()
    {
        var ticketTypeId = await factory.SeedTicketTypeAsync(100, maxPerUser: 2);
        var client = factory.CreateClientFor(Guid.NewGuid());

        var ok = await client.SendAsync(PlaceOrder(ticketTypeId, 2, Guid.NewGuid().ToString()));
        var tooMany = await client.SendAsync(PlaceOrder(ticketTypeId, 1, Guid.NewGuid().ToString()));

        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await factory.Services.GetRequiredService<InventoryStore>()
            .GetAvailableAsync(ticketTypeId)).Should().Be(98);
    }

    private sealed record OrderResponse(Guid Id, string Status);
}
using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;

namespace TicketNow.Booking.Api.BackgroundServices;

public sealed class InventoryRebuilder(
    IServiceScopeFactory scopes,
    InventoryStore store,
    ILogger<InventoryRebuilder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        var held = await db.OrderItems
            .Where(i => i.Order.Status == OrderStatus.AwaitingPayment || i.Order.Status == OrderStatus.Confirmed)
            .GroupBy(i => new { i.TicketTypeId, i.Order.UserId })
            .Select(g => new { g.Key.TicketTypeId, g.Key.UserId, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);

        var items = await db.Inventory.AsNoTracking().ToListAsync(ct);
        var restored = 0;

        foreach (var inv in items)
        {
            var used = held.Where(h => h.TicketTypeId == inv.TicketTypeId).Sum(h => h.Qty);
            if (await store.InitAvailableAsync(inv.TicketTypeId, inv.Total - used, overwrite: false))
                restored++;
        }

        foreach (var h in held)
            await store.InitUserHeldAsync(h.TicketTypeId, h.UserId, h.Qty, overwrite: false);

        log.LogInformation("Inventory rebuild: {Restored}/{Total} counter được khôi phục từ Postgres",
            restored, items.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
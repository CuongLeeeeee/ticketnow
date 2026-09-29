using MassTransit;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.Contracts.Catalog;

namespace TicketNow.Booking.Api.Consumers;

public sealed class TicketTypePublishedConsumer(BookingDbContext db, InventoryStore inventory)
    : IConsumer<TicketTypePublished>
{
    public async Task Consume(ConsumeContext<TicketTypePublished> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        // An upsert is already idempotent: applying it twice gives the same result, so no inbox is needed
        var item = await db.Inventory.FindAsync([m.TicketTypeId], ct);
        if (item is null)
        {
            item = new InventoryItem { TicketTypeId = m.TicketTypeId };
            db.Inventory.Add(item);
        }

        item.EventId = m.EventId;
        item.Name = m.Name;
        item.Price = m.Price;
        item.Currency = m.Currency;
        item.MaxPerUser = m.MaxPerUser;
        item.SalesStartAt = m.SalesStartAt.ToUniversalTime();
        item.Total = m.TotalQuantity;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await inventory.InitAvailableAsync(item.TicketTypeId, item.Total - item.Sold, overwrite: false);
    }
}
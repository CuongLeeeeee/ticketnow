using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;

namespace TicketNow.Booking.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/inventory").RequireAuthorization("admin");
        admin.MapGet("/{ticketTypeId:guid}", GetInventory);
        return app;
    }

    // Compares the Redis counter with what Postgres says is left, to spot drift
    private static async Task<IResult> GetInventory(Guid ticketTypeId, BookingDbContext db, InventoryStore inventory)
    {
        var item = await db.Inventory.AsNoTracking().FirstOrDefaultAsync(x => x.TicketTypeId == ticketTypeId);
        if (item is null) return Results.NotFound();

        var held = await db.OrderItems
            .Where(i => i.TicketTypeId == ticketTypeId &&
                        (i.Order.Status == OrderStatus.AwaitingPayment || i.Order.Status == OrderStatus.Confirmed))
            .SumAsync(i => (int?)i.Quantity) ?? 0;

        var inRedis = await inventory.GetAvailableAsync(ticketTypeId);

        return Results.Ok(new
        {
            item.Name,
            item.Total,
            HeldOrSold = held,
            AvailableInDb = item.Total - held,
            AvailableInRedis = inRedis,
            InSync = inRedis == item.Total - held
        });
    }
}

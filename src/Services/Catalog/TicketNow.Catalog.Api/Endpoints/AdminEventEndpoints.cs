using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TicketNow.Catalog.Domain;
using TicketNow.Catalog.Infrastructure.Persistence;
using TicketNow.Contracts.Catalog;

namespace TicketNow.Catalog.Api.Endpoints;

public static class AdminEventEndpoints
{
    public static IEndpointRouteBuilder MapAdminEventEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/events").RequireAuthorization("admin");
        admin.MapPost("", CreateEvent);
        admin.MapPost("/{id:guid}/ticket-types", AddTicketType);
        admin.MapPost("/{id:guid}/publish", Publish);
        return app;
    }

    private static async Task<IResult> CreateEvent(CreateEventRequest req, CatalogDbContext db)
    {
        var ev = new Event
        {
            Title = req.Title,
            Artist = req.Artist,
            Venue = req.Venue,
            StartsAt = req.StartsAt.ToUniversalTime(),
            SalesStartAt = req.SalesStartAt.ToUniversalTime()
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return Results.Created($"/api/events/{ev.Id}", new { ev.Id });
    }

    private static async Task<IResult> AddTicketType(Guid id, CreateTicketTypeRequest req, CatalogDbContext db)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id)) return Results.NotFound();

        var tt = new TicketType
        {
            EventId = id,
            Name = req.Name,
            Price = req.Price,
            Currency = req.Currency,
            TotalQuantity = req.TotalQuantity,
            MaxPerUser = req.MaxPerUser
        };
        db.TicketTypes.Add(tt);
        await db.SaveChangesAsync();
        return Results.Created($"/api/events/{id}", new { tt.Id });
    }

    // Publishing emits TicketTypePublished per ticket type; Booking uses it to build its inventory
    private static async Task<IResult> Publish(Guid id, CatalogDbContext db, IDistributedCache cache)
    {
        var ev = await db.Events.Include(e => e.TicketTypes).FirstOrDefaultAsync(e => e.Id == id);
        if (ev is null) return Results.NotFound();
        if (ev.TicketTypes.Count == 0) return Results.BadRequest("The event has no ticket types");

        ev.Status = EventStatus.Published;

        foreach (var tt in ev.TicketTypes)
        {
            db.OutboxMessages.Add(OutboxMessage.From(new TicketTypePublished(
                tt.Id, ev.Id, tt.Name, tt.Price, tt.Currency,
                tt.TotalQuantity, tt.MaxPerUser, ev.SalesStartAt)));
        }

        await db.SaveChangesAsync();
        await cache.RemoveAsync(EventEndpoints.CacheKey(id));
        return Results.NoContent();
    }
}

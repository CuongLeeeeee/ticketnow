using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TicketNow.Catalog.Domain;
using TicketNow.Catalog.Infrastructure.Persistence;

namespace TicketNow.Catalog.Api.Endpoints;

public static class EventEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events");
        events.MapGet("", ListEvents);
        events.MapGet("/{id:guid}", GetEvent);
        return app;
    }

    private static async Task<IResult> ListEvents(CatalogDbContext db, int page = 1, int size = 20)
    {
        var items = await db.Events.AsNoTracking()
            .Where(e => e.Status == EventStatus.Published)
            .OrderBy(e => e.StartsAt)
            .Skip((page - 1) * size).Take(size)
            .Select(e => new EventListItem(e.Id, e.Title, e.Artist, e.Venue, e.StartsAt))
            .ToListAsync();

        return Results.Ok(items);
    }

    // Cached for 60s; publishing an event evicts its entry (see AdminEventEndpoints.Publish)
    private static async Task<IResult> GetEvent(Guid id, CatalogDbContext db, IDistributedCache cache)
    {
        var key = CacheKey(id);

        var cached = await cache.GetStringAsync(key);
        if (cached is not null)
            return Results.Content(cached, "application/json");

        var ev = await db.Events.AsNoTracking()
            .Include(e => e.TicketTypes)
            .FirstOrDefaultAsync(e => e.Id == id && e.Status == EventStatus.Published);

        if (ev is null) return Results.NotFound();

        var dto = new EventDetail(ev.Id, ev.Title, ev.Artist, ev.Venue, ev.StartsAt, ev.SalesStartAt,
            ev.TicketTypes.Select(t =>
                new TicketTypeDto(t.Id, t.Name, t.Price, t.Currency, t.TotalQuantity, t.MaxPerUser)).ToList());

        var json = JsonSerializer.Serialize(dto, WebJson);
        await cache.SetStringAsync(key, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
        });

        return Results.Content(json, "application/json");
    }

    internal static string CacheKey(Guid eventId) => $"event:{eventId}";
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TicketNow.Catalog.Domain;
using TicketNow.Catalog.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");

// ---------- Public ----------
var events = app.MapGroup("/api/events");

events.MapGet("", async (CatalogDbContext db, int page = 1, int size = 20) =>
{
    var items = await db.Events.AsNoTracking()
        .Where(e => e.Status == EventStatus.Published)
        .OrderBy(e => e.StartsAt)
        .Skip((page - 1) * size).Take(size)
        .Select(e => new EventListItem(e.Id, e.Title, e.Artist, e.Venue, e.StartsAt))
        .ToListAsync();

    return Results.Ok(items);
});

events.MapGet("/{id:guid}", async (Guid id, CatalogDbContext db, IDistributedCache cache) =>
{
    var key = $"event:{id}";

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

    var json = JsonSerializer.Serialize(dto);
    await cache.SetStringAsync(key, json, new DistributedCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    });

    return Results.Content(json, "application/json");
});


var admin = app.MapGroup("/api/admin/events");

admin.MapPost("", async (CreateEventRequest req, CatalogDbContext db) =>
{
    var ev = new Event
    {
        Title = req.Title,
        Artist = req.Artist,
        Venue = req.Venue,
        StartsAt = req.StartsAt,
        SalesStartAt = req.SalesStartAt
    };
    db.Events.Add(ev);
    await db.SaveChangesAsync();
    return Results.Created($"/api/events/{ev.Id}", new { ev.Id });
});

admin.MapPost("/{id:guid}/ticket-types", async (Guid id, CreateTicketTypeRequest req, CatalogDbContext db) =>
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
});

admin.MapPost("/{id:guid}/publish", async (Guid id, CatalogDbContext db, IDistributedCache cache) =>
{
    var ev = await db.Events.Include(e => e.TicketTypes).FirstOrDefaultAsync(e => e.Id == id);
    if (ev is null) return Results.NotFound();
    if (ev.TicketTypes.Count == 0) return Results.BadRequest("Sự kiện chưa có loại vé nào");

    ev.Status = EventStatus.Published;

    
    foreach (var tt in ev.TicketTypes)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = "TicketTypePublished",
            Payload = JsonSerializer.Serialize(new
            {
                TicketTypeId = tt.Id,
                EventId = ev.Id,
                Quantity = tt.TotalQuantity,
                tt.MaxPerUser
            })
        });
    }

    await db.SaveChangesAsync();
    await cache.RemoveAsync($"event:{id}");
    return Results.NoContent();
});

app.Run();

record EventListItem(Guid Id, string Title, string Artist, string Venue, DateTimeOffset StartsAt);
record TicketTypeDto(Guid Id, string Name, decimal Price, string Currency, int TotalQuantity, int MaxPerUser);
record EventDetail(Guid Id, string Title, string Artist, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset SalesStartAt, List<TicketTypeDto> TicketTypes);
record CreateEventRequest(string Title, string Artist, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset SalesStartAt);
record CreateTicketTypeRequest(string Name, decimal Price, string Currency, int TotalQuantity, int MaxPerUser);
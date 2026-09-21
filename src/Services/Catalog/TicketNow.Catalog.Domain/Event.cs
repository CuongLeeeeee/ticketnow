using System.Net.Sockets;

namespace TicketNow.Catalog.Domain;

public enum EventStatus { Draft, Published, Cancelled }

public class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = default!;
    public string Artist { get; set; } = default!;
    public string Venue { get; set; } = default!;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset SalesStartAt { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TicketType> TicketTypes { get; set; } = [];
}
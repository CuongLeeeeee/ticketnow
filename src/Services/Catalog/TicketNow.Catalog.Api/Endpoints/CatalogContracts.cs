namespace TicketNow.Catalog.Api.Endpoints;

public record EventListItem(Guid Id, string Title, string Artist, string Venue, DateTimeOffset StartsAt);
public record TicketTypeDto(Guid Id, string Name, decimal Price, string Currency, int TotalQuantity, int MaxPerUser);
public record EventDetail(Guid Id, string Title, string Artist, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset SalesStartAt, List<TicketTypeDto> TicketTypes);
public record CreateEventRequest(string Title, string Artist, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset SalesStartAt);
public record CreateTicketTypeRequest(string Name, decimal Price, string Currency, int TotalQuantity, int MaxPerUser);

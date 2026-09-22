namespace TicketNow.Contracts.Catalog;

public record TicketTypePublished(
    Guid TicketTypeId,
    Guid EventId,
    string Name,
    decimal Price,
    string Currency,
    int TotalQuantity,
    int MaxPerUser,
    DateTimeOffset SalesStartAt);
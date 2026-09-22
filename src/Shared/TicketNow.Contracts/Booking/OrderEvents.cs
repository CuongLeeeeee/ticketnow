namespace TicketNow.Contracts.Booking;

public record OrderLine(Guid TicketTypeId, int Quantity, decimal UnitPrice);

public record OrderPlaced(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<OrderLine> Lines);

public record OrderCancelled(Guid OrderId, Guid UserId, string Reason);
using System.Security.Claims;

namespace TicketNow.Booking.Api.Endpoints;

public record PlaceOrderRequest(List<PlaceOrderItem> Items);
public record PlaceOrderItem(Guid TicketTypeId, int Quantity);
public record OrderItemDto(Guid TicketTypeId, int Quantity, decimal UnitPrice);
public record OrderDto(Guid Id, Guid EventId, string Status, decimal TotalAmount, string Currency,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, List<OrderItemDto> Items);
public record WaitingRoomSettings(bool Enabled, int AdmitPerTick, int AdmitTtlSeconds);

internal static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);
}

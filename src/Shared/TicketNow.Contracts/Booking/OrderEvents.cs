namespace TicketNow.Contracts.Booking;

public static class ReleaseReasons
{
    public const string UserCancelled = "UserCancelled";
    public const string Expired = "Expired";
    public const string PaymentFailed = "PaymentFailed";
}

public record OrderLine(Guid TicketTypeId, int Quantity, decimal UnitPrice);

public record OrderPlaced(
    Guid OrderId, Guid UserId, Guid EventId, decimal TotalAmount, string Currency,
    DateTimeOffset ExpiresAt, IReadOnlyList<OrderLine> Lines);

public record OrderConfirmed(Guid OrderId, Guid UserId, Guid EventId, decimal TotalAmount, string Currency);

// Phát ra mỗi khi đơn rời AwaitingPayment mà KHÔNG thành công => vé phải được trả lại
public record OrderReleased(Guid OrderId, Guid UserId, string Reason, IReadOnlyList<OrderLine> Lines);

public record PaymentRefundRequested(Guid OrderId, Guid UserId, string Reason);
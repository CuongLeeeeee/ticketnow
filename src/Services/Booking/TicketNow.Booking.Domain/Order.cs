namespace TicketNow.Booking.Domain;

public enum OrderStatus { AwaitingPayment, Confirmed, Expired, Cancelled, PaymentFailed }

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid EventId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.AwaitingPayment;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "VND";
    public string IdempotencyKey { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = default!;
    public Guid TicketTypeId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
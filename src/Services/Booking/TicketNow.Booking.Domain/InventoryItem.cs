namespace TicketNow.Booking.Domain;

public class InventoryItem
{
    public Guid TicketTypeId { get; set; }
    public Guid EventId { get; set; }
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "VND";
    public int MaxPerUser { get; set; }
    public DateTimeOffset SalesStartAt { get; set; }
    public int Total { get; set; }
    public int Sold { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
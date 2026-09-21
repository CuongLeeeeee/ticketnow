namespace TicketNow.Catalog.Domain;

public class TicketType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string Name { get; set; } = default!;      // VIP, Standard, Early bird
    public decimal Price { get; set; }
    public string Currency { get; set; } = "VND";
    public int TotalQuantity { get; set; }
    public int MaxPerUser { get; set; } = 4;
}
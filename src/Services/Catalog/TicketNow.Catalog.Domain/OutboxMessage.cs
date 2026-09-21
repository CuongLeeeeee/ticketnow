namespace TicketNow.Catalog.Domain;

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}
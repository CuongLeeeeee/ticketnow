
using System.Text.Json;

namespace TicketNow.Identity.Api.Domain;

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    public static OutboxMessage From<T>(T message) where T : notnull => new()
    {
        Type = typeof(T).Name,
        Payload = JsonSerializer.Serialize(message)
    };
}
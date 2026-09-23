using Microsoft.EntityFrameworkCore;

namespace TicketNow.BuildingBlocks.Messaging;

public class InboxMessage
{
    public Guid MessageId { get; set; }
    public string Consumer { get; set; } = default!;
    public DateTimeOffset ProcessedAt { get; set; }
}

public static class InboxExtensions
{
    public static ModelBuilder AddInbox(this ModelBuilder b)
    {
        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            // Khóa kép: cùng một message có thể được NHIỀU consumer trong service xử lý
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(200);
        });
        return b;
    }

    /// <summary>Gọi BÊN TRONG transaction của consumer. Trả false nếu message này đã xử lý rồi.</summary>
    public static async Task<bool> TryRegisterInboxAsync(
        this DbContext db, Guid messageId, string consumer, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO inbox_messages (message_id, consumer, processed_at)
            VALUES ({messageId}, {consumer}, now())
            ON CONFLICT DO NOTHING
            """, ct);
        return rows == 1;
    }
}
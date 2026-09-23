using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketNow.Booking.Domain;
using TicketNow.BuildingBlocks.Messaging;

namespace TicketNow.Booking.Infrastructure.Persistence;

public class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<Domain.Order> Orders => Set<Domain.Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<InventoryItem>(e =>
        {
            e.ToTable("inventory", t =>
                t.HasCheckConstraint("ck_inventory_sold_range", "sold >= 0 AND sold <= total"));
            e.HasKey(x => x.TicketTypeId);
            e.Property(x => x.TicketTypeId).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.EventId);
        });

        b.Entity<Domain.Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();   // lớp idempotency thứ hai
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasMany(x => x.Items)
             .WithOne(i => i.Order)
             .HasForeignKey(i => i.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderItem>(e =>
        {
            e.ToTable("order_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.HasIndex(x => x.TicketTypeId);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.ProcessedAt);
        });

        b.AddInbox();
    }
}
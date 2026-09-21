using Microsoft.EntityFrameworkCore;
using TicketNow.Catalog.Domain;

namespace TicketNow.Catalog.Infrastructure.Persistence;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Event>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Artist).HasMaxLength(200).IsRequired();
            e.Property(x => x.Venue).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.StartsAt);
            e.HasMany(x => x.TicketTypes)
             .WithOne()
             .HasForeignKey(t => t.EventId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TicketType>(e =>
        {
            e.ToTable("ticket_types");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.ProcessedAt);
        });
    }
}
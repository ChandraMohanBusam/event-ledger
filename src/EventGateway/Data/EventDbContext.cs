using EventGateway.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventGateway.Data;

/// <summary>
/// The Gateway's own event store. Independent of the Account Service database.
/// </summary>
public sealed class EventDbContext(DbContextOptions<EventDbContext> options)
    : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var evt = modelBuilder.Entity<Event>();

        // EventId is the primary key and the idempotency key. The unique
        // constraint is the source of truth for "have we seen this event".
        evt.HasKey(e => e.EventId);
        evt.Property(e => e.EventId).HasMaxLength(100);

        evt.Property(e => e.AccountId).HasMaxLength(100);
        evt.HasIndex(e => e.AccountId);

        evt.Property(e => e.Type)
            .HasConversion<string>()
            .HasMaxLength(10);

        evt.Property(e => e.Amount).HasPrecision(18, 2);
        evt.Property(e => e.Currency).HasMaxLength(3);
    }
}

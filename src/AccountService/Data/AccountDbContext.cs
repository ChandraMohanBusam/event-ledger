using AccountService.Domain;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Data;

/// <summary>
/// The Account Service's own database context. Independent of the Gateway:
/// no shared schema, no shared state.
/// </summary>
public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options)
    : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var transaction = modelBuilder.Entity<Transaction>();

        // TransactionId is the primary key and the idempotency key. The unique
        // constraint is the source of truth: a duplicate insert fails at the
        // database, which is how idempotency stays correct even under
        // concurrent duplicate requests.
        transaction.HasKey(t => t.TransactionId);
        transaction.Property(t => t.TransactionId).HasMaxLength(100);

        transaction.Property(t => t.AccountId).HasMaxLength(100);
        transaction.HasIndex(t => t.AccountId);

        // Stored as text (CREDIT / DEBIT) for readability rather than an int.
        transaction.Property(t => t.Type)
            .HasConversion<string>()
            .HasMaxLength(10);

        transaction.Property(t => t.Amount).HasPrecision(18, 2);
        transaction.Property(t => t.Currency).HasMaxLength(3);
    }
}

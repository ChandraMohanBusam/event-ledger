using AccountService.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Tests;

/// <summary>
/// Creates an isolated SQLite in-memory database per test, using a private
/// (non-shared) connection so tests cannot interfere with one another. Tests
/// run against real SQLite, not EF InMemory, so the unique constraint that
/// enforces idempotency is genuinely exercised.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public AccountDbContext Context { get; }

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AccountDbContext(options);
        Context.Database.EnsureCreated();
    }

    /// <summary>Returns a fresh context over the same connection, to verify persisted state.</summary>
    public AccountDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AccountDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}

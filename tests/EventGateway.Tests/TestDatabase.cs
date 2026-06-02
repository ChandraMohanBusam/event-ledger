using EventGateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventGateway.Tests;

/// <summary>
/// Isolated SQLite in-memory EventDbContext per test, over a private connection.
/// Tests run against real SQLite so the unique constraint behind idempotency is
/// genuinely exercised.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public EventDbContext Context { get; }

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new EventDbContext(options);
        Context.Database.EnsureCreated();
    }

    public EventDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new EventDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}

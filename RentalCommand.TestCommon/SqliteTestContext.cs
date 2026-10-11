using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

/// <summary>
/// Disposable test helper that opens a kept-alive SQLite in-memory connection,
/// creates a <see cref="RentalCommandDbContext"/> backed by that connection, calls
/// <see cref="DatabaseFacade.EnsureCreated"/>, and seeds the minimum required rows
/// (a <see cref="Portfolio"/> with Id = 1).
///
/// Use one instance per test method so every test starts with an isolated schema.
/// </summary>
public sealed class SqliteTestContext : IDisposable
{
    private readonly SqliteConnection _conn;

    public RentalCommandDbContext Db { get; }
    public string ConnectionString => _conn.ConnectionString;
    public SqliteConnection Connection => _conn;

    public SqliteTestContext(IEnumerable<IInterceptor>? interceptors = null)
    {
        // Keep the connection open for the lifetime of the test so the in-memory DB persists.
        _conn = new SqliteConnection($"Data Source=test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _conn.Open();

        var optionsBuilder = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn);

        if (interceptors is not null)
            optionsBuilder.AddInterceptors(interceptors);

        var options = optionsBuilder.Options;

        Db = new SqliteCompatibleRentalCommandDbContext(options);
        Db.Database.EnsureCreated();

        // Seed the minimum required anchor row.
        Db.Portfolios.Add(new Portfolio
        {
            Id                    = 1,
            Name                  = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone              = "UTC",
            CreatedAt             = DateTime.UtcNow,
            UpdatedAt             = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}

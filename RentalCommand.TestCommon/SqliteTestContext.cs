using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    public SqliteTestContext()
    {
        // Keep the connection open for the lifetime of the test so the in-memory DB persists.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        Db = new AutomationTestDbContext(options);
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

/// <summary>
/// Derived DbContext that strips Postgres-specific DDL (jsonb column types, check constraints,
/// and the partial unique index filter on Payments) so the schema is valid on SQLite.
/// </summary>
internal sealed class AutomationTestDbContext : RentalCommandDbContext
{
    public AutomationTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- jsonb → TEXT (SQLite has no jsonb type) ----
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<SecurityDepositHolding>().Property(e => e.DeductionsJson).HasColumnType("TEXT");

        // ---- Remove Postgres check constraints that SQLite cannot execute ----
        // Calling ToTable() without a builder action replaces the existing table configuration
        // (including check constraints) with a plain mapping.
        modelBuilder.Entity<Lease>().ToTable("Leases");
        modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");

        // ---- Replace the partial unique index on Payments ----
        // HasFilter("\"PeriodKey\" IS NOT NULL") is Postgres syntax; SQLite ignores it
        // but EnsureCreated may still fail depending on the provider version.
        // Replace with a plain (non-filtered) unique index — the service's own AnyAsync
        // check is what we are testing, not the DB constraint.
        modelBuilder.Entity<Payment>()
            .HasIndex(p => new { p.LeaseId, p.PaymentType, p.PeriodKey })
            .IsUnique()
            .HasFilter(null);

        // ---- Replace the partial unique index on AutopayEnrollments ----
        // HasFilter("\"Active\" = true") is Postgres syntax. Drop the filter for SQLite; the
        // service's own AnyAsync(Active) check is what the tests exercise.
        modelBuilder.Entity<AutopayEnrollment>()
            .HasIndex(e => e.LeaseId)
            .IsUnique()
            .HasFilter(null);
    }
}

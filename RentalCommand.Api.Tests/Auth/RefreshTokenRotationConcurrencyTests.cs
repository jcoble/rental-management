using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// M-3 + L-7 regression: refresh-token rotation must be transactional and correct across processes.
///
/// The reuse-detection + grace + single-flight were process-static dictionaries, so across multiple API
/// instances two concurrent refreshes of the same token could both rotate (lost update), or a straggler
/// landing on a different replica would miss the in-process grace and falsely trip the family-revoke.
///
/// These tests exercise the DB-backed path (atomic conditional claim + persisted GraceExpiresAt) by giving
/// each "replica" its own DbContext and JwtTokenService instance and clearing the process-static maps, so
/// the only thing preventing a double-rotation / false revoke is the database-level guarantee.
/// </summary>
public sealed class RefreshTokenRotationConcurrencyTests
{
    public RefreshTokenRotationConcurrencyTests() => JwtTokenService.ResetInProcessStateForTests();

    [Fact]
    public async Task Concurrent_refresh_of_same_token_rotates_exactly_once()
    {
        using var harness = new RotationHarness();
        var presented = await harness.SeedUserAndIssueRefreshTokenAsync();

        // Two independent "replicas" race to refresh the SAME presented token simultaneously.
        var replicaA = harness.NewReplica();
        var replicaB = harness.NewReplica();

        var results = await Task.WhenAll(
            replicaA.RefreshTokenAsync(presented),
            replicaB.RefreshTokenAsync(presented));

        // Neither concurrent refresh may be rejected: a benign double-submit must not trip the family-revoke.
        results.Should().OnlyContain(r => r != null, "a legitimate concurrent refresh must never be rejected");

        // The presented token row is rotated exactly once (used+revoked), not double-rotated.
        var presentedRow = await harness.VerificationDb.RefreshTokens
            .SingleAsync(rt => rt.TokenHash == Hash(presented));
        presentedRow.IsUsed.Should().BeTrue();
        presentedRow.IsRevoked.Should().BeTrue();
        presentedRow.GraceExpiresAt.Should().NotBeNull("rotation stamps the persisted reuse-grace deadline");

        // At least one usable successor was minted; the family was NOT revoked (the user still has live tokens).
        var liveTokens = await harness.VerificationDb.RefreshTokens.CountAsync(rt => !rt.IsRevoked);
        liveTokens.Should().BeGreaterThan(0, "a concurrent double-submit must leave the user with a working session");
    }

    [Fact]
    public async Task Straggler_within_grace_on_another_replica_gets_a_pair_without_family_revoke()
    {
        using var harness = new RotationHarness();
        var presented = await harness.SeedUserAndIssueRefreshTokenAsync();

        // Replica A rotates the token first.
        var first = await harness.NewReplica().RefreshTokenAsync(presented);
        first.Should().NotBeNull();

        // Clear the in-PROCESS grace so the cross-process (persisted GraceExpiresAt) path is what's exercised.
        JwtTokenService.ResetInProcessStateForTests();

        // Replica B (separate instance) presents the now-rotated token a moment later, still within grace.
        var straggler = await harness.NewReplica().RefreshTokenAsync(presented);

        straggler.Should().NotBeNull("a straggler within the persisted grace window must be re-served, not revoked");
        straggler!.AccessToken.Should().NotBeNullOrEmpty();

        // The family must remain intact (no hard revoke-everything triggered by the benign straggler).
        var liveTokens = await harness.VerificationDb.RefreshTokens.CountAsync(rt => !rt.IsRevoked);
        liveTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Genuine_reuse_after_grace_revokes_the_whole_family()
    {
        using var harness = new RotationHarness();
        var presented = await harness.SeedUserAndIssueRefreshTokenAsync();

        // Rotate it legitimately.
        var first = await harness.NewReplica().RefreshTokenAsync(presented);
        first.Should().NotBeNull();

        // Force the grace window closed (simulate a replay arriving well after rotation) and clear in-proc state.
        await harness.ExpireAllGraceWindowsAsync();
        JwtTokenService.ResetInProcessStateForTests();

        // Replaying the consumed token now is genuine reuse/theft → family revoke, refresh rejected.
        var replay = await harness.NewReplica().RefreshTokenAsync(presented);

        replay.Should().BeNull("reuse outside the grace window is theft and must be rejected");
        var liveTokens = await harness.VerificationDb.RefreshTokens.CountAsync(rt => !rt.IsRevoked);
        liveTokens.Should().Be(0, "theft detection revokes every token in the family");
    }

    private static string Hash(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Owns one shared in-memory SQLite database (kept alive by a held-open connection) plus a real
    /// Identity stack, and hands out fresh DbContext + JwtTokenService pairs that act as independent
    /// API replicas all pointing at that same database.
    /// </summary>
    private sealed class RotationHarness : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
        private readonly DbContextOptions<RentalCommandDbContext> _options;
        private readonly List<IDisposable> _disposables = new();
        private int _userId;

        public RentalCommandDbContext VerificationDb { get; }

        public RotationHarness()
        {
            _conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _options = new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(_conn).Options;

            using (var seed = NewDb())
            {
                seed.Database.EnsureCreated();
            }

            VerificationDb = NewDb();
        }

        public async Task<string> SeedUserAndIssueRefreshTokenAsync()
        {
            var replica = NewReplica(out var db, out _);
            var user = new ApplicationUser
            {
                UserName = "rotate@test.local",
                Email = "rotate@test.local",
                EmailConfirmed = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            _userId = user.Id;

            var tokens = await replica.GenerateTokensAsync(user, new List<string>());
            return tokens.RefreshToken;
        }

        public IJwtTokenService NewReplica() => NewReplica(out _, out _);

        public IJwtTokenService NewReplica(out RentalCommandDbContext db, out UserManager<ApplicationUser> userManager)
        {
            db = NewDb();
            userManager = NewUserManager(db);
            var settings = Options.Create(new JwtSettings
            {
                SecretKey = "test-signing-key-that-is-長-enough-32+chars!!",
                Issuer = "rc-tests",
                Audience = "rc-tests",
                AccessTokenExpirationMinutes = 15,
                RefreshTokenExpirationDays = 7,
            });
            return new JwtTokenService(settings, db, userManager, NullLogger<JwtTokenService>.Instance);
        }

        public async Task ExpireAllGraceWindowsAsync()
        {
            await VerificationDb.RefreshTokens
                .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.GraceExpiresAt, DateTime.UtcNow.AddMinutes(-5)));
        }

        private RentalCommandDbContext NewDb()
        {
            var db = new SqliteRotationDbContext(_options);
            _disposables.Add(db);
            return db;
        }

        private UserManager<ApplicationUser> NewUserManager(RentalCommandDbContext db)
        {
            var store = new UserStore<ApplicationUser, IdentityRole<int>, RentalCommandDbContext, int>(db);
            _disposables.Add(store);
            var options = Options.Create(new IdentityOptions());
            var pwdHasher = new PasswordHasher<ApplicationUser>();
            var userValidators = new List<IUserValidator<ApplicationUser>>();
            var pwdValidators = new List<IPasswordValidator<ApplicationUser>>();
            var manager = new UserManager<ApplicationUser>(
                store, options, pwdHasher, userValidators, pwdValidators,
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);
            _disposables.Add(manager);
            return manager;
        }

        public void Dispose()
        {
            VerificationDb.Dispose();
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
            _conn.Dispose();
        }
    }

    /// <summary>SQLite-compatible context (strips Postgres-only DDL), same pattern as SqliteTestContext.</summary>
    private sealed class SqliteRotationDbContext : RentalCommandDbContext
    {
        public SqliteRotationDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
            modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
            modelBuilder.Entity<SecurityDepositHolding>().Property(e => e.DeductionsJson).HasColumnType("TEXT");
            modelBuilder.Entity<Lease>().ToTable("Leases");
            modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
            modelBuilder.Entity<Payment>()
                .HasIndex(p => new { p.LeaseId, p.PaymentType, p.PeriodKey }).IsUnique().HasFilter(null);
            modelBuilder.Entity<AutopayEnrollment>()
                .HasIndex(e => e.LeaseId).IsUnique().HasFilter(null);
        }
    }
}

using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the unique-key race where ON CONFLICT waits for another transaction whose
/// committed row is not visible to the first statement snapshot.
/// </summary>
public sealed class LegalDocumentSourceVersionConcurrencyTests : IAsyncLifetime
{
    private const string LoserApplicationName = "legal-source-resolution-loser";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    private SharedPostgreSqlDatabase? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private int _portfolioId;
    private int _propertyId;
    private int _templateId;
    private int _actorUserId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Model);
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext(_connectionString);
        await db.Database.EnsureCreatedAsync();

        var actor = new ApplicationUser
        {
            UserName = "legal-source@example.test",
            NormalizedUserName = "LEGAL-SOURCE@EXAMPLE.TEST",
            Email = "legal-source@example.test",
            NormalizedEmail = "LEGAL-SOURCE@EXAMPLE.TEST",
            DisplayName = "Legal source test actor",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = CreatedAtUtc,
        };
        var portfolio = new Portfolio
        {
            Name = "Legal source concurrency",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = CreatedAtUtc,
            UpdatedAt = CreatedAtUtc,
        };
        db.AddRange(actor, portfolio);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = "Concurrency House",
            AddressLine1 = "1 Snapshot Way",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = CreatedAtUtc,
            UpdatedAt = CreatedAtUtc,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Concurrent lease template",
            Version = 1,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = CreatedAtUtc,
        };
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync();

        _actorUserId = actor.Id;
        _portfolioId = portfolio.Id;
        _propertyId = property.Id;
        _templateId = template.Id;
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Authored_resolution_replays_inside_the_same_transaction_after_invisible_concurrent_winner()
    {
        Skip.IfNot(_dockerAvailable,
            "Docker is not available; Postgres legal-source concurrency verification skipped.");

        var businessKey = $"template:{_templateId}:v1";
        await using var winnerConnection = new NpgsqlConnection(_connectionString);
        await winnerConnection.OpenAsync();
        await using var winnerTransaction =
            await winnerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var winnerId = await InsertUncommittedWinnerAsync(
            winnerConnection, winnerTransaction, businessKey);

        var loserConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = LoserApplicationName,
        }.ConnectionString;
        await using var loserDb = NewContext(loserConnectionString);
        await using var loserTransaction = await loserDb.Database.BeginTransactionAsync();
        var transactionIdBefore = await CurrentTransactionIdAsync(loserDb);

        var auditScope = new AtomicAuditScope(TimeProvider.System);
        var attemptId = Guid.NewGuid();
        var commandContext = new AtomicCommandContext(loserDb, auditScope, TimeProvider.System);
        commandContext.BeginAttempt(attemptId);
        try
        {
            using var attemptLease = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.resolve-legal-source", Guid.NewGuid().ToString("N")),
                attemptId,
                loserDb);

            var resolutionTask = AtomicLeaseMutationPersistence.ResolveAuthoredDocumentSourceVersionAsync(
                loserDb,
                commandContext,
                _portfolioId,
                _propertyId,
                0,
                _templateId,
                _actorUserId,
                CreatedAtUtc.AddMinutes(1));

            await WaitForLoserLockAsync();
            await winnerTransaction.CommitAsync();

            var result = await resolutionTask.WaitAsync(TimeSpan.FromSeconds(10));
            var transactionIdAfter = await CurrentTransactionIdAsync(loserDb);

            result.Resolved.Should().BeTrue();
            result.DocumentSourceVersionId.Should().Be(winnerId);
            transactionIdAfter.Should().Be(transactionIdBefore,
                "the fresh statement snapshot must remain inside the command's original transaction");

            await loserTransaction.RollbackAsync();
        }
        finally
        {
            commandContext.EndAttempt();
        }

        await using var verify = NewContext(_connectionString);
        var matching = await verify.LegalDocumentSourceVersions
            .Where(source => source.PortfolioId == _portfolioId && source.BusinessKey == businessKey)
            .Select(source => source.Id)
            .ToListAsync();
        matching.Should().Equal(winnerId);
    }

    private async Task<int> InsertUncommittedWinnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string businessKey)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "LegalDocumentSourceVersions"
                ("PublicId", "PortfolioId", "SourceKind", "BusinessKey",
                 "DocumentTemplateId", "DocumentTemplateVersion", "RendererKey", "RendererVersion",
                 "SnapshotPayload", "CreatedAtUtc", "CreatedByUserId")
            VALUES
                (gen_random_uuid(), @portfolioId, 'AuthoredTemplateSnapshot', @businessKey,
                 @templateId, 1, 'overlay', 1, '{}'::jsonb, @createdAtUtc, @actorUserId)
            RETURNING "Id"
            """, connection, transaction);
        command.Parameters.AddWithValue("portfolioId", _portfolioId);
        command.Parameters.AddWithValue("businessKey", businessKey);
        command.Parameters.AddWithValue("templateId", _templateId);
        command.Parameters.AddWithValue("createdAtUtc", CreatedAtUtc);
        command.Parameters.AddWithValue("actorUserId", _actorUserId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task WaitForLoserLockAsync()
    {
        await using var monitor = new NpgsqlConnection(_connectionString);
        await monitor.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @applicationName
                      AND wait_event_type = 'Lock')
                """, monitor);
            command.Parameters.AddWithValue("applicationName", LoserApplicationName);
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The losing resolver never blocked on the concurrent source insert.");
    }

    private static Task<long> CurrentTransactionIdAsync(RentalCommandDbContext db) =>
        db.Database.SqlQueryRaw<long>("SELECT txid_current() AS \"Value\"").SingleAsync();

    private static RentalCommandDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connectionString)
            .Options);
}

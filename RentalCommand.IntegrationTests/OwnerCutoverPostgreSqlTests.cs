using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class OwnerCutoverPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const string PreCutoverMigration = "20260716140000_AddWorkspaceLlmCredentials";
    private const string CutoverMigration = "20260716160000_RemoveLegacyOwnerAndRouteAliases";
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public OwnerCutoverPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task LegacyOwner_IsAbsentAfterMigration()
    {
        const string schemaSql = """
            SELECT
              to_regclass('public."Owners"') IS NULL
                AND NOT EXISTS (
                  SELECT 1
                  FROM information_schema.columns
                  WHERE table_schema = 'public'
                    AND table_name = 'Properties'
                    AND column_name IN ('OwnerId', 'OwnerEntityId'))
                AND NOT EXISTS (
                  SELECT 1
                  FROM information_schema.columns
                  WHERE table_schema = 'public'
                    AND table_name = 'OwnerEntities'
                    AND column_name = 'Address')
                AND to_regclass('public."PropertyOwnerships"') IS NOT NULL;
            """;
        CaptureSql("LEGACY_OWNER_SCHEMA_ABSENCE", schemaSql);

        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync();

        (await ScalarAsync<bool>(connection, schemaSql)).Should().BeTrue(
            "the destructive cutover must leave only OwnerEntity plus PropertyOwnership");
    }

    [Fact]
    public async Task CanonicalOwnership_RetainsRowsConstraintsIndexesAndRls()
    {
        await RunAtPreCutoverBoundaryAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var portfolio = new Portfolio
            {
                Name = "Owner cutover portfolio",
                ManagementCompanyName = "Owner Cutover Co",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var canonicalOwner = new OwnerEntity
            {
                Portfolio = portfolio,
                Name = "Existing Canonical Owner LLC",
                AddressLine1 = "10 Canonical Way",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                Email = "canonical-owner@example.test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var canonicalProperty = Property(
                portfolio, "Canonical relationship property", now);
            var legacyProperty = Property(
                portfolio, "Legacy relationship property", now.AddMinutes(1));
            db.AddRange(canonicalOwner, canonicalProperty, legacyProperty);
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                FoundationBaselinePostgreSql.CreatePropertyOwnershipInfrastructureSql);
            var canonicalOwnership = new PropertyOwnership
            {
                PortfolioId = portfolio.Id,
                PropertyId = canonicalProperty.Id,
                OwnerEntityId = canonicalOwner.Id,
                OwnershipSharePercent = 62.5m,
                EffectiveFromUtc = now.AddDays(-30),
                StatementRecipientName = "Canonical Statements Team",
                StatementRecipientEmail = "statements@example.test",
                PayeeName = "Canonical Property Payee LLC",
            };
            db.PropertyOwnerships.Add(canonicalOwnership);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var ownershipBeforeCutover = await db.PropertyOwnerships
                .AsNoTracking()
                .SingleAsync(ownership =>
                    ownership.Id == canonicalOwnership.Id
                    && ownership.PortfolioId == portfolio.Id);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Owners" (
                  "PortfolioId", "Name", "Email", "Phone", "MailingAddress",
                  "Notes", "CreatedAt", "UpdatedAt")
                VALUES (
                  {portfolio.Id}, {"Legacy Owner LLC"}, {"legacy-owner@example.test"},
                  {"614-555-0100"}, {"20 Legacy Lane"}, {"Preserve me"}, {now}, {now});

                UPDATE "Properties"
                SET "OwnerEntityId" = {canonicalOwner.Id}
                WHERE "Id" = {canonicalProperty.Id}
                  AND "PortfolioId" = {portfolio.Id};

                UPDATE "Properties"
                SET "OwnerId" = (
                  SELECT "Id"
                  FROM "Owners"
                  WHERE "PortfolioId" = {portfolio.Id}
                    AND "Name" = {"Legacy Owner LLC"})
                WHERE "Id" = {legacyProperty.Id}
                  AND "PortfolioId" = {portfolio.Id};
                """);

            await db.GetService<IMigrator>().MigrateAsync(CutoverMigration);
            db.ChangeTracker.Clear();

            var ownershipAfterCutover = await db.PropertyOwnerships
                .AsNoTracking()
                .SingleAsync(ownership =>
                    ownership.Id == canonicalOwnership.Id
                    && ownership.PortfolioId == portfolio.Id);
            ownershipAfterCutover.Should().BeEquivalentTo(ownershipBeforeCutover);
            ownershipAfterCutover.PropertyId.Should().Be(canonicalProperty.Id);
            ownershipAfterCutover.OwnerEntityId.Should().Be(canonicalOwner.Id);
            ownershipAfterCutover.OwnershipSharePercent.Should().Be(62.5m);
            ownershipAfterCutover.StatementRecipientName.Should().Be("Canonical Statements Team");
            ownershipAfterCutover.StatementRecipientEmail.Should().Be("statements@example.test");
            ownershipAfterCutover.PayeeName.Should().Be("Canonical Property Payee LLC");

            (await db.PropertyOwnerships
                .AsNoTracking()
                .CountAsync(ownership => ownership.PortfolioId == portfolio.Id))
                .Should().Be(1, "legacy direct ownership must not be copied");
            (await db.OwnerEntities
                .AsNoTracking()
                .AnyAsync(owner =>
                    owner.PortfolioId == portfolio.Id
                    && owner.Name == "Legacy Owner LLC"))
                .Should().BeFalse("legacy Owners must not be converted to OwnerEntities");

            var retainedOwner = await db.OwnerEntities
                .AsNoTracking()
                .SingleAsync(owner =>
                    owner.Id == canonicalOwner.Id
                    && owner.PortfolioId == portfolio.Id);
            retainedOwner.Name.Should().Be(canonicalOwner.Name);
            retainedOwner.AddressLine1.Should().Be(canonicalOwner.AddressLine1);
            retainedOwner.Email.Should().Be(canonicalOwner.Email);

            const string constraintSql = """
                SELECT conname
                FROM pg_catalog.pg_constraint
                WHERE conrelid = 'public."PropertyOwnerships"'::regclass
                ORDER BY conname;
                """;
            const string indexSql = """
                SELECT indexname
                FROM pg_catalog.pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'PropertyOwnerships'
                ORDER BY indexname;
                """;
            const string rlsSql = """
                SELECT relrowsecurity AND relforcerowsecurity
                FROM pg_catalog.pg_class
                WHERE oid = 'public."PropertyOwnerships"'::regclass;
                """;
            const string policySql = """
                SELECT policyname
                FROM pg_catalog.pg_policies
                WHERE schemaname = 'public'
                  AND tablename = 'PropertyOwnerships'
                ORDER BY policyname;
                """;
            const string grantsSql = """
                SELECT
                  has_table_privilege(
                    'rentalcommand_api', 'public."PropertyOwnerships"',
                    'SELECT,INSERT,UPDATE,DELETE')
                  AND has_table_privilege(
                    'rentalcommand_engine', 'public."PropertyOwnerships"', 'SELECT');
                """;
            CaptureSql("CANONICAL_OWNERSHIP_CONSTRAINTS", constraintSql);
            CaptureSql("CANONICAL_OWNERSHIP_INDEXES", indexSql);
            CaptureSql("CANONICAL_OWNERSHIP_RLS", rlsSql);
            CaptureSql("CANONICAL_OWNERSHIP_POLICIES", policySql);
            CaptureSql("CANONICAL_OWNERSHIP_GRANTS", grantsSql);

            await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
            await connection.OpenAsync();
            var constraints = await ReadStringsAsync(connection, constraintSql);
            constraints.Should().Contain(
            [
                "PK_PropertyOwnerships",
                "CK_PropertyOwnerships_EffectivePeriod",
                "CK_PropertyOwnerships_OwnershipSharePercent",
                "FK_PropertyOwnerships_OwnerEntities_OwnerEntityId_PortfolioId",
                "FK_PropertyOwnerships_Properties_PropertyId_PortfolioId",
                "EX_PropertyOwnerships_NoOwnerPropertyOverlap",
            ]);

            var indexes = await ReadStringsAsync(connection, indexSql);
            indexes.Should().Contain(
            [
                "IX_PropertyOwnerships_Portfolio_OwnerEntity_EffectiveFromUtc",
                "IX_PropertyOwnerships_PortfolioId_PropertyId_EffectiveFromUtc",
                "IX_PropertyOwnerships_PropertyId_OwnerEntityId_EffectiveToUtc",
            ]);
            (await ScalarAsync<bool>(connection, rlsSql)).Should().BeTrue();
            (await ReadStringsAsync(connection, policySql)).Should().BeEquivalentTo(
            [
                "tenant_delete", "tenant_insert", "tenant_select", "tenant_update",
            ]);
            (await ScalarAsync<bool>(connection, grantsSql)).Should().BeTrue();
        });
    }

    [Fact]
    public async Task OwnershipQueries_PageInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var now = DateTime.UtcNow;
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie" })
        {
            var owner = new OwnerEntity
            {
                PortfolioId = PortfolioId,
                Name = $"Cutover Paging Owner {name}",
                Email = $"{name.ToLowerInvariant()}-owner@example.test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var property = Property(null, $"{name} Cutover Property", now);
            property.PortfolioId = PortfolioId;
            property.Ownerships.Add(new PropertyOwnership
            {
                PortfolioId = PortfolioId,
                OwnerEntity = owner,
                OwnershipSharePercent = 100m,
                EffectiveFromUtc = now.AddDays(-1),
                StatementRecipientName = $"{name} Statements",
                StatementRecipientEmail = $"{name.ToLowerInvariant()}-statements@example.test",
                PayeeName = $"{name} Payee LLC",
            });
            _context.Db.Properties.Add(property);
        }
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var service = new PropertyService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        var page = await service.ListPageAsync(scope, new PropertyListQuery
        {
            Search = "Cutover Paging Owner",
            Sort = "-name",
            Skip = 1,
            Take = 1,
        });

        page.TotalCount.Should().Be(3);
        page.Items.Should().ContainSingle(item => item.Name == "Bravo Cutover Property");
        page.Items[0].Ownerships.Should().ContainSingle(ownership =>
            ownership.OwnerName == "Cutover Paging Owner Bravo"
            && ownership.OwnershipSharePercent == 100m
            && ownership.StatementRecipientName == "Bravo Statements"
            && ownership.PayeeName == "Bravo Payee LLC");
        _commands.Should().HaveCount(
            2,
            "authorization, ownership filtering, aggregation, sorting, and paging stay in the translated count/page statements");

        for (var index = 0; index < _commands.Count; index++)
        {
            CaptureSql($"OWNERSHIP_PAGE_{index + 1}", _commands[index]);
        }

        var countSql = _commands.Single(IsTopLevelCountCommand);
        countSql.Should().Contain("AuthSessions");
        countSql.Should().Contain("PropertyOwnerships");
        countSql.Should().Contain("ILIKE");
        countSql.Should().Contain("WHERE");
        countSql.Should().ContainEquivalentOf("join");

        var pageSql = _commands.Single(IsBoundedPageCommand);
        pageSql.Should().Contain("AuthSessions");
        pageSql.Should().Contain("PropertyOwnerships");
        pageSql.Should().Contain("OwnerEntities");
        pageSql.Should().Contain("ILIKE");
        pageSql.Should().Contain("WHERE");
        pageSql.Should().ContainEquivalentOf("join");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("OFFSET");
        pageSql.Should().ContainEquivalentOf("COUNT", "unit aggregation must stay in the page SQL");
    }

    private async Task RunAtPreCutoverBoundaryAsync(
        Func<RentalCommandDbContext, Task> test)
    {
        var databaseName = $"rc_owner_cutover_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(_context.ConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var boundaryConnectionString = new NpgsqlConnectionStringBuilder(_context.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

        await ExecuteNonQueryAsync(
            adminConnectionString,
            $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(boundaryConnectionString)
                .Options;
            await using var db = new RentalCommandDbContext(options);
            await db.Database.MigrateAsync(PreCutoverMigration);
            await test(db);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var now = DateTime.UtcNow;
        var email = $"owner-cutover-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Owner cutover verifier",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        return new WorkspaceReadScope(
            PortfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static Property Property(Portfolio? portfolio, string name, DateTime now) => new()
    {
        Portfolio = portfolio,
        Name = name,
        AddressLine1 = $"{name} Street",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private void CaptureSql(string label, string sql)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(sql);
    }

    private static bool IsTopLevelCountCommand(string sql) =>
        sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedPageCommand(string sql) =>
        !IsTopLevelCountCommand(sql)
        && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

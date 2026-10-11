using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Import;
using RentalCommand.Data;
using RentalCommand.Data.Import;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL regression coverage for CSV import authorization CTE alias scope.</summary>
public sealed class AtomicCsvImportAliasPostgreSqlTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _postgres;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task UnitPreview_ExecutesAuthorizationCteWithoutAliasFailure_AndReturnsValidRow()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var scenario = await SeedScenarioAsync(db, "unit-alias");
        var persistence = new AtomicUnitImportPersistence(db);

        var result = await persistence.PreviewAsync(
            scenario.Scope,
            [
                new AtomicUnitImportRow(
                    1,
                    scenario.PropertyId,
                    null,
                    "101",
                    2,
                    1,
                    1250m,
                    []),
            ]);

        result.Authorized.Should().BeTrue();
        result.TotalRows.Should().Be(1);
        result.ValidRows.Should().Be(1);
        result.Rows.Should().ContainSingle(row =>
            row.Valid
            && row.PropertyId == scenario.PropertyId
            && row.UnitNumber == "101");
    }

    [SkippableTheory]
    [InlineData(PropertyType.SingleFamily, false)]
    [InlineData(PropertyType.MultiFamily, false)]
    [InlineData(PropertyType.Condo, false)]
    [InlineData(PropertyType.Townhome, false)]
    [InlineData(PropertyType.Storage, true)]
    [InlineData(PropertyType.Parking, true)]
    [InlineData(PropertyType.Commercial, true)]
    public async Task UnitPreview_PreservesMissingBedsAndBathsThroughPropertyTypeValidation(
        PropertyType propertyType,
        bool expectedValid)
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var scenario = await SeedScenarioAsync(db, $"unit-details-{propertyType}");
        var property = await db.Properties.SingleAsync(row => row.Id == scenario.PropertyId);
        property.PropertyType = propertyType;
        await db.SaveChangesAsync();

        var result = await new AtomicUnitImportPersistence(db).PreviewAsync(
            scenario.Scope,
            [new AtomicUnitImportRow(
                1,
                scenario.PropertyId,
                null,
                "101",
                null,
                null,
                1250m,
                [])]);

        result.Rows.Should().ContainSingle().Which.Valid.Should().Be(expectedValid);
        if (expectedValid)
            result.Rows.Single().Errors.Should().BeEmpty();
        else
            result.Rows.Single().Errors.Should().Contain(
                "Bedrooms and bathrooms are required for residential dwellings.");
    }

    [SkippableFact]
    public async Task CorePropertyPreview_ExecutesAuthorizationCteWithoutAliasFailure_AndReturnsValidRow()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var scenario = await SeedScenarioAsync(db, "core-property-alias");
        var persistence = new AtomicCoreCsvImportPersistence(db);

        var result = await persistence.PreviewAsync(
            scenario.Scope,
            AtomicCoreCsvImportDomain.Property,
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    RowNumber = 1,
                    Name = "Alias Proof Property",
                    AddressLine1 = "100 Alias Way",
                    AddressLine2 = (string?)null,
                    City = "Columbus",
                    State = "OH",
                    PostalCode = "43215",
                    PropertyType = (int)PropertyType.MultiFamily,
                    RentalStructure = nameof(RentalStructure.MultiRental),
                    UnitNumber = "A",
                    Errors = Array.Empty<string>(),
                },
            }));

        result.Authorized.Should().BeTrue();
        result.TotalRows.Should().Be(1);
        result.ValidRows.Should().Be(1);
        result.Rows.Should().ContainSingle(row => row.Valid && row.RowNumber == 1);
        result.CreatedCount.Should().Be(0);
    }

    private async Task<Scenario> SeedScenarioAsync(RentalCommandDbContext db, string suffix)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = $"csv-alias-{suffix}@example.test",
            NormalizedUserName = $"CSV-ALIAS-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"csv-alias-{suffix}@example.test",
            NormalizedEmail = $"CSV-ALIAS-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = "CSV Alias Proof",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"CSV alias {suffix}",
            ManagementCompanyName = "Alias Management",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Seed Property {suffix}",
            AddressLine1 = "1 Seed Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.AddRange(property, membership);
        await db.SaveChangesAsync();

        db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();

        return new Scenario(
            property.Id,
            new WorkspaceReadScope(
                portfolio.Id,
                user.Id,
                session.Id,
                accessContext.Id,
                accessContext.AccessRevision));
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for CSV import alias PostgreSQL tests.");

    private sealed record Scenario(int PropertyId, WorkspaceReadScope Scope);
}

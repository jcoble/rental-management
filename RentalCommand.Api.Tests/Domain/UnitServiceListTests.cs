using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class UnitServiceListTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 9002;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly UnitService _sut;

    public UnitServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        SeedCanonicalLeaseReadModel();
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(), TimeProvider.System);
    }

    private void SeedCanonicalLeaseReadModel()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Users.Add(new ApplicationUser
        {
            Id = ActorUserId,
            PortfolioId = PortfolioId,
            UserName = "unit-tests@rentalcommand.local",
            NormalizedUserName = "UNIT-TESTS@RENTALCOMMAND.LOCAL",
            Email = "unit-tests@rentalcommand.local",
            NormalizedEmail = "UNIT-TESTS@RENTALCOMMAND.LOCAL",
            DisplayName = "Unit Test Actor",
            CreatedAt = now,
        });
        _ctx.Db.SaveChanges();

        _ctx.Db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_unit_occupancy" AS
            SELECT
                u."PortfolioId",
                u."PropertyId",
                u."Id" AS "UnitId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                CASE WHEN lm."Id" IS NULL THEN 0 ELSE 1 END AS "IsOccupied",
                lm."Id" AS "CurrentLeaseManagementId",
                0 AS "HasScheduledMoveIn",
                NULL AS "NextPlannedPossessionAtUtc",
                NULL AS "PlannedLeaseManagementId",
                0 AS "IsInTurnover",
                0 AS "IsOutOfService",
                0 AS "IsOnManagementHold",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                NULL AS "OccupancyExceptionCode"
            FROM "Units" u
            LEFT JOIN "LeaseManagements" lm
              ON lm."PortfolioId" = u."PortfolioId"
             AND lm."UnitId" = u."Id"
             AND lm."PossessionGivenAtUtc" IS NOT NULL
             AND lm."PossessionReturnedAtUtc" IS NULL
             AND lm."CanceledAtUtc" IS NULL;

            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT
                lm."PortfolioId",
                lm."PropertyId",
                lm."UnitId",
                lm."Id" AS "LeaseManagementId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                date('now') AS "BusinessDate",
                CASE WHEN lm."NoticeGivenAtUtc" IS NOT NULL THEN 'Ending' ELSE 'Occupied' END AS "Lifecycle",
                (SELECT la."Id" FROM "LeaseAgreements" la
                  WHERE la."PortfolioId" = lm."PortfolioId"
                    AND la."LeaseManagementId" = lm."Id"
                  ORDER BY la."VersionNumber" DESC LIMIT 1) AS "CurrentAgreementId",
                NULL AS "UpcomingAgreementId",
                0 AS "CurrentPartyCount",
                0 AS "CurrentResidentCount",
                0 AS "CurrentFinanciallyResponsiblePartyCount",
                NULL AS "CurrentPrimaryPartyId",
                NULL AS "CurrentPrimaryTenantId",
                NULL AS "CurrentPrimaryTenantName",
                NULL AS "TenantAccountId",
                0 AS "HasMissingTenantAccount",
                0 AS "HasMultipleGoverningAgreements",
                0 AS "HasMultipleCurrentPrimaryTenants",
                0 AS "HasAccountCloseMismatch",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                0 AS "HasReconciliationException"
            FROM "LeaseManagements" lm;
            """);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListWithHealthPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedUnit("A", "Cedar Point Flats", openWorkOrders: 1);
        SeedUnit("B", "Cedar Point Flats", openWorkOrders: 3);
        SeedUnit("C", "Harbor View Apartments", openWorkOrders: 0);
        SeedUnit("D", "Harbor View Apartments", openWorkOrders: 2);

        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery
        {
            Sort = "-openWorkOrderCount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(u => u.UnitNumber).Should().Equal("D", "A");
        result.Items.Select(u => u.OpenWorkOrderCount).Should().Equal(2, 1);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Units\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListWithHealthPageAsync_AppliesStatusAndStageFiltersInSqlWindow()
    {
        var now = DateTime.UtcNow;
        var tenant = SeedTenant(now);
        var (renewalProperty, renewalUnit) = SeedUnitShell("2A", "Cedar Point Flats", now);
        SeedRelationship(renewalProperty, renewalUnit, tenant, now, now.AddDays(45));

        var (activeProperty, activeUnit) = SeedUnitShell("3B", "Cedar Point Flats", now);
        SeedRelationship(activeProperty, activeUnit, tenant, now, now.AddDays(180));
        SeedUnitShell("4C", "Harbor View Apartments", now);
        _ctx.Db.SaveChanges();

        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery
        {
            Status = "Occupied",
            Stage = "Renewal",
            Sort = "unitNumber",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].UnitNumber.Should().Be("2A");
        result.Items[0].SimpleStage.Should().Be("Renewal");

        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("FROM \"Units\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListWithHealthPageAsync_NoticeGivenOccupiedUnitDoesNotFallBackToVacant()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell("101", "Westview Four-Plex", now);
        var tenant = SeedTenant(now);
        SeedRelationship(property, unit, tenant, now, now.AddDays(30), noticeGiven: true);
        _ctx.Db.SaveChanges();

        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery());

        var row = result.Items.Should().ContainSingle(u => u.Id == unit.Id).Subject;
        row.Status.Should().Be(DerivedUnitStatus.Occupied.ToString());
        row.SimpleStage.Should().Be("Move-Out");
    }

    [Fact]
    public async Task ListAsync_AvailableForLeaseReturnsOnlyVacantUnitsWithoutOccupyingLeasesInSql()
    {
        var now = DateTime.UtcNow;
        var (_, availableUnit) = SeedUnitShell("101", "Available Property", now);
        var (occupiedProperty, occupiedUnit) = SeedUnitShell("102", "Occupied Property", now);
        var (leasedProperty, leasedUnit) = SeedUnitShell("103", "Leased Property", now);
        var tenant = SeedTenant(now);
        SeedRelationship(occupiedProperty, occupiedUnit, tenant, now, now.AddYears(1));
        SeedRelationship(leasedProperty, leasedUnit, tenant, now, now.AddYears(1), noticeGiven: true);
        _ctx.Db.SaveChanges();

        _commands.Clear();
        var result = await _sut.ListAsync(PortfolioId, null, new UnitListQuery
        {
            AvailableForLease = true,
            Sort = "unitNumber",
        });

        result.Select(u => u.Id).Should().Equal(availableUnit.Id);
        _commands.Should().ContainSingle(sql =>
            sql.Contains("Units", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListWithHealthPageAsync_DocsCountMatchesUnitDashboardRollupDefinition()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell("2A", "Maple Heights", now);
        var tenant = SeedTenant(now);
        var relationship = SeedRelationship(property, unit, tenant, now, now.AddMonths(10));
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Repair",
            Description = "Open repair",
            Status = WorkOrderStatus.New,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
            UpdatedAt = now,
        };
        var directExpense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Description = "Unit receipt",
            Amount = 35m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrderExpense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            WorkOrder = workOrder,
            Description = "Repair receipt",
            Amount = 65m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var inspection = new Inspection
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Type = InspectionType.MoveIn,
            Status = InspectionStatus.Completed,
            ScheduledFor = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(workOrder, directExpense, workOrderExpense, inspection);
        _ctx.Db.SaveChanges();

        var agreementFile = StoredFile("LeaseAgreement", relationship.Agreement.Id, "lease.pdf", now);
        _ctx.Db.StoredFiles.AddRange(
            StoredFile("Unit", unit.Id, "unit-photo.jpg", now),
            agreementFile,
            StoredFile("Expense", directExpense.Id, "unit-receipt.jpg", now),
            StoredFile("Expense", workOrderExpense.Id, "repair-receipt.jpg", now),
            StoredFile("WorkOrder", workOrder.Id, "repair-photo.jpg", now),
            StoredFile("Inspection", inspection.Id, "inspection.pdf", now),
            StoredFile("Tenant", tenant.Id, "tenant-only.pdf", now));
        _ctx.Db.SaveChanges();
        var artifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = agreementFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
            StorageKey = agreementFile.FilePath,
            FileName = agreementFile.FileName,
            ContentType = agreementFile.ContentType,
            ByteLength = agreementFile.FileSize,
            ContentSha256 = new string('a', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.LegalDocumentArtifacts.Add(artifact);
        _ctx.Db.SaveChanges();
        relationship.Agreement.IssuedArtifactId = artifact.Id;
        relationship.Agreement.IssuedAtUtc = now;
        relationship.Agreement.ExecutedArtifactId = artifact.Id;
        relationship.Agreement.FullyExecutedAtUtc = now;
        _ctx.Db.SaveChanges();

        _commands.Clear();
        var list = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery());
        var listSql = _commands.ToList();
        var dashboard = await new UnitDashboardService(_ctx.Db, new AuditDescriber(), new AuditDiffBuilder(), TimeProvider.System)
            .GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        var row = list.Items.Should().ContainSingle().Subject;
        dashboard.Should().NotBeNull();
        row.DocsNeedingReviewCount.Should().Be(6);
        row.DocsNeedingReviewCount.Should().Be(dashboard!.Header.DocsNeedingReviewCount);
        listSql.Should().Contain(sql =>
            sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LeaseAgreements", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LegalDocumentArtifacts", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Inspections", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedUnit(string unitNumber, string propertyName, int openWorkOrders)
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell(unitNumber, propertyName, now);

        for (var i = 0; i < openWorkOrders; i++)
        {
            _ctx.Db.WorkOrders.Add(new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                Title = $"{unitNumber} repair {i}",
                Description = "Open repair",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = $"{unitNumber} closed repair",
            Description = "Closed repair",
            Status = WorkOrderStatus.Completed,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private (LeaseManagement Relationship, LeaseAgreement Agreement) SeedRelationship(
        Property property,
        Unit unit,
        Tenant tenant,
        DateTime now,
        DateTime endAt,
        bool noticeGiven = false)
    {
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{unit.Id}-{Guid.NewGuid():N}",
            PlannedPossessionAtUtc = now.AddMonths(-2),
            PossessionGivenAtUtc = now.AddMonths(-2),
            NoticeGivenAtUtc = noticeGiven ? now.AddDays(-7) : null,
            PlannedMoveOutAtUtc = noticeGiven ? endAt : null,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(relationship);
        _ctx.Db.SaveChanges();

        var startOn = DateOnly.FromDateTime(now.AddMonths(-2));
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{Guid.NewGuid():N}"[..12],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = startOn,
            TermEndOn = DateOnly.FromDateTime(endAt),
            GoverningFromOn = startOn,
            BaseRentAmount = unit.MarketRent,
            RentDueDay = 1,
            SecurityDepositObligation = unit.MarketRent,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
        };
        _ctx.Db.AddRange(
            agreement,
            new LeaseManagementParty
            {
                PortfolioId = PortfolioId,
                LeaseManagementId = relationship.Id,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = startOn,
                ChangeReason = "Unit list fixture",
                CreatedAtUtc = now,
                CreatedByUserId = ActorUserId,
            });
        _ctx.Db.SaveChanges();
        return (relationship, agreement);
    }

    private (Property Property, Unit Unit) SeedUnitShell(
        string unitNumber,
        string propertyName,
        DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            MarketRent = 1250m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

        return (property, unit);
    }

    private Tenant SeedTenant(DateTime now)
    {
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Jordan",
            LastName = "Smith",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private static StoredFile StoredFile(string entityType, int entityId, string fileName, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        EntityType = entityType,
        EntityId = entityId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "image/jpeg",
        FileSize = 1024,
        UploadedAt = now,
    };

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

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

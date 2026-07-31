using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public class UnitServiceListTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;

    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private UnitService _sut = null!;
    private WorkspaceReadScope _scope;

    public UnitServiceListTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        _scope = SeedCanonicalLeaseReadModel();
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(),
            TimeProvider.System, Mock.Of<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
    }

    private WorkspaceReadScope SeedCanonicalLeaseReadModel()
    {
        var now = DateTime.UtcNow;
        var user = _ctx.Db.Users.Single(candidate => candidate.Id == ActorUserId);

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

        _ctx.Db.AddRange(assignment, session);
        _ctx.Db.SaveChanges();
        _ctx.Db.Entry(accessContext).Reload();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ListWithHealthPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedUnit("A", "Cedar Point Flats", openWorkOrders: 1);
        SeedUnit("B", "Cedar Point Flats", openWorkOrders: 3);
        SeedUnit("C", "Harbor View Apartments", openWorkOrders: 0);
        SeedUnit("D", "Harbor View Apartments", openWorkOrders: 2);

        await ActivateApiScopeAsync();
        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(_scope, new UnitHealthListQuery
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

        await ActivateApiScopeAsync();
        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(_scope, new UnitHealthListQuery
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

        _commands.Should().HaveCount(3);
        _commands.Count(sql =>
                sql.Contains("FROM \"Units\"", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase))
            .Should().Be(2);
        _commands.Should().ContainSingle(sql =>
            sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
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

        await ActivateApiScopeAsync();
        var result = await _sut.ListWithHealthPageAsync(_scope, new UnitHealthListQuery());

        var row = result.Items.Should().ContainSingle(u => u.Id == unit.Id).Subject;
        row.Status.Should().Be(DerivedUnitStatus.Occupied.ToString());
        row.SimpleStage.Should().Be("Move-Out");
    }

    [Fact]
    public async Task ListPageAsync_AvailableForLeaseFiltersCountsAndPagesInSql()
    {
        var now = DateTime.UtcNow;
        var (_, availableUnit) = SeedUnitShell("101", "Available Property", now);
        var (_, excludedAvailableUnit) = SeedUnitShell("104", "Excluded Available Property", now);
        var (occupiedProperty, occupiedUnit) = SeedUnitShell("102", "Occupied Property", now);
        var (leasedProperty, leasedUnit) = SeedUnitShell("103", "Leased Property", now);
        var tenant = SeedTenant(now);
        SeedRelationship(occupiedProperty, occupiedUnit, tenant, now, now.AddYears(1));
        SeedRelationship(leasedProperty, leasedUnit, tenant, now, now.AddYears(1), noticeGiven: true);
        _ctx.Db.SaveChanges();

        var availability = await _ctx.Db.UnitOccupancyProjections
            .AsNoTracking()
            .Where(row => row.UnitId == availableUnit.Id)
            .SingleAsync();
        availability.PortfolioId.Should().Be(PortfolioId);
        availability.IsOccupied.Should().BeFalse();
        availability.HasScheduledMoveIn.Should().BeFalse();
        availability.IsInTurnover.Should().BeFalse();
        availability.IsOutOfService.Should().BeFalse();
        availability.IsOnManagementHold.Should().BeFalse();
        await ActivateApiScopeAsync();

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, null, new UnitListQuery
        {
            AvailableForLease = true,
            ExcludeUnitId = excludedAvailableUnit.Id,
            Sort = "unitNumber",
            Skip = 0,
            Take = 1,
        });

        result.TotalCount.Should().Be(1);
        result.Skip.Should().Be(0);
        result.Take.Should().Be(1);
        result.Items.Select(u => u.Id).Should().Equal(availableUnit.Id);
        _commands.Should().HaveCount(2);
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Units", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("Units", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListWithHealthPageAsync_DocsCountMatchesUnitDashboardRollupDefinition()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell("2A", "Maple Heights", now);
        var tenant = SeedTenant(now);
        SeedRelationship(property, unit, tenant, now, now.AddMonths(10));
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
            OperationalScope = ExpenseOperationalScope.Unit,
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
            OperationalScope = ExpenseOperationalScope.WorkOrder,
            Property = property,
            Unit = unit,
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

        _ctx.Db.StoredFiles.AddRange(
            StoredFile("Unit", unit.Id, "unit-photo.jpg", now),
            StoredFile("Expense", directExpense.Id, "unit-receipt.jpg", now),
            StoredFile("Expense", workOrderExpense.Id, "repair-receipt.jpg", now),
            StoredFile("WorkOrder", workOrder.Id, "repair-photo.jpg", now),
            StoredFile("Inspection", inspection.Id, "inspection.pdf", now),
            StoredFile("Tenant", tenant.Id, "tenant-only.pdf", now));
        _ctx.Db.SaveChanges();

        await ActivateApiScopeAsync();
        _commands.Clear();
        var list = await _sut.ListWithHealthPageAsync(_scope, new UnitHealthListQuery());
        var listSql = _commands.ToList();
        var dashboard = await new UnitDashboardService(_ctx.Db, new AuditDescriber(), new AuditDiffBuilder(), TimeProvider.System)
            .GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        var row = list.Items.Should().ContainSingle().Subject;
        dashboard.Should().NotBeNull();
        row.DocsNeedingReviewCount.Should().Be(7);
        row.DocsNeedingReviewCount.Should().Be(dashboard!.Header.DocsNeedingReviewCount);
        listSql.Should().HaveCount(3);
        listSql.Should().Contain(sql =>
            sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LeaseAgreements", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LegalDocumentArtifacts", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Inspections", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
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
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, ActorUserId, now),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = startOn,
            ChangeReason = "Unit list fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{Guid.NewGuid():N}"[..12],
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-2),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.AddRange(agreement, party, account);
        _ctx.Db.SaveChanges();

        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        });
        _ctx.Db.SaveChanges();

        var (issuedArtifact, executedArtifact) = SeedAgreementArtifacts(relationship.Id, now);
        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now;
        _ctx.Db.SaveChanges();
        return (relationship, agreement);
    }

    private (LegalDocumentArtifact Issued, LegalDocumentArtifact Executed) SeedAgreementArtifacts(
        int relationshipId,
        DateTime now)
    {
        var issuedFile = StoredFile(null, null, $"agreement-{relationshipId}-issued.pdf", now);
        var executedFile = StoredFile(null, null, $"agreement-{relationshipId}-executed.pdf", now);
        _ctx.Db.StoredFiles.AddRange(issuedFile, executedFile);
        _ctx.Db.SaveChanges();

        var issuedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = issuedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = issuedFile.FilePath,
            FileName = issuedFile.FileName,
            ContentType = issuedFile.ContentType,
            ByteLength = issuedFile.FileSize,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var executedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = executedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
            StorageKey = executedFile.FilePath,
            FileName = executedFile.FileName,
            ContentType = executedFile.ContentType,
            ByteLength = executedFile.FileSize,
            ContentSha256 = new string('c', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _ctx.Db.SaveChanges();
        return (issuedArtifact, executedArtifact);
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
            PortfolioId = PortfolioId,
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

    private async Task ActivateApiScopeAsync()
    {
        await _ctx.Db.Database.OpenConnectionAsync();
        await _ctx.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {_scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {_scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {_scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {_scope.AccessRevision.ToString()}, false);
            """);
    }

    private Tenant SeedTenant(DateTime now)
    {
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Jordan",
            LastName = "Smith",
            Email = "jordan.smith@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private static StoredFile StoredFile(string? entityType, long? entityId, string fileName, DateTime now) => new()
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

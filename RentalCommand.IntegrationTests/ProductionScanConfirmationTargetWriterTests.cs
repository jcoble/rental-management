using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Scanning;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL coverage for the production scan target writers.</summary>
public sealed class ProductionScanConfirmationTargetWriterTests : IAsyncLifetime
{
    private static readonly DateTime CommandTime =
        new(2026, 7, 11, 14, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<ConfirmScanDraftResult> Codec =
        new("scan-confirm.result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _propertyId;
    private int _unitId;
    private int _tenantId;
    private int _leaseManagementId;
    private int _tenantAccountId;
    private int _vendorId;
    private int _workOrderId;
    private int _actorUserId;
    private Guid _authSessionId;
    private int _accessContextId;
    private long _accessRevision;
    private readonly Dictionary<int, string> _preparedFingerprints = [];

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable) return;

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("rentalcommand")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddScoped<ProductionScanConfirmationTargetWriter>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<
            ConfirmScanDraftCommand,
            ConfirmScanDraftResult,
            ConfirmScanDraftHandler<ProductionScanConfirmationTargetWriter>>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres.GetConnectionString())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await using var scope = Scope();
        await scope.Db.Database.EnsureCreatedAsync();
        await scope.Db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await scope.Db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await scope.Db.Database.ExecuteSqlRawAsync(LeaseManagementLifecycleViewSql.Create);
        await scope.Db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
        var actor = new ApplicationUser
        {
            UserName = "scan-writer@example.test",
            NormalizedUserName = "SCAN-WRITER@EXAMPLE.TEST",
            Email = "scan-writer@example.test",
            NormalizedEmail = "SCAN-WRITER@EXAMPLE.TEST",
            DisplayName = "Scan writer",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = CommandTime,
        };
        var portfolio = new Portfolio
        {
            Name = "Production scan writers",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        scope.Db.AddRange(actor, portfolio);
        await scope.Db.SaveChangesAsync();
        _actorUserId = actor.Id;
        _portfolioId = portfolio.Id;

        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = "Scan House",
            AddressLine1 = "1 Main St",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        var tenant = new Tenant
        {
            PortfolioId = _portfolioId,
            FirstName = "Alex",
            LastName = "Renter",
            Email = "alex@example.test",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        var vendor = new Vendor
        {
            PortfolioId = _portfolioId,
            Name = "Existing Vendor",
            ServiceType = "General",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        scope.Db.AddRange(property, tenant, vendor);
        await scope.Db.SaveChangesAsync();
        _propertyId = property.Id;
        _tenantId = tenant.Id;
        _vendorId = vendor.Id;

        var unit = new Unit
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            UnitNumber = "1",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        scope.Db.Units.Add(unit);
        await scope.Db.SaveChangesAsync();
        _unitId = unit.Id;

        var accessContext = new WorkspaceAccessContext
        {
            UserId = _actorUserId,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = CommandTime,
            UpdatedAtUtc = CommandTime,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = CommandTime.AddDays(-1),
            CreatedAtUtc = CommandTime,
            UpdatedAtUtc = CommandTime,
        };
        scope.Db.WorkspaceMemberships.Add(membership);
        await scope.Db.SaveChangesAsync();

        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = _portfolioId,
            RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = CommandTime.AddDays(-1),
            CreatedAtUtc = CommandTime,
            UpdatedAtUtc = CommandTime,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = _actorUserId,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = CommandTime,
            LastSeenAtUtc = CommandTime,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            UnitId = _unitId,
            RelationshipNumber = "LM-SCAN-BASE",
            CreatedAtUtc = CommandTime,
            UpdatedAtUtc = CommandTime,
            CreatedByUserId = _actorUserId,
        };
        scope.Db.AddRange(assignment, session, relationship);
        await scope.Db.SaveChangesAsync();

        var tenantAccount = new TenantAccount
        {
            PortfolioId = _portfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = "TA-SCAN-BASE",
            Currency = "USD",
            OpenedAtUtc = CommandTime,
            CreatedAtUtc = CommandTime,
            CreatedByUserId = _actorUserId,
        };
        var primaryParty = new LeaseManagementParty
        {
            PortfolioId = _portfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = _tenantId,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(CommandTime.AddDays(-1)),
            ChangeReason = "Canonical work-order scan fixture",
            CreatedAtUtc = CommandTime,
            CreatedByUserId = _actorUserId,
        };
        scope.Db.AddRange(tenantAccount, primaryParty);
        await scope.Db.SaveChangesAsync();
        _tenantAccountId = tenantAccount.Id;
        _leaseManagementId = relationship.Id;

        var workOrder = new WorkOrder
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            UnitId = _unitId,
            TenantId = _tenantId,
            LeaseManagementId = _leaseManagementId,
            VendorId = _vendorId,
            Title = "Scan-linked repair",
            Description = "Fixture work order for scanned expense receipts.",
            Category = "Maintenance",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = CommandTime,
            CreatedBy = "test",
            UpdatedAt = CommandTime,
        };
        scope.Db.WorkOrders.Add(workOrder);
        await scope.Db.SaveChangesAsync();
        _workOrderId = workOrder.Id;

        _authSessionId = session.Id;
        _accessContextId = accessContext.Id;
        _accessRevision = accessContext.AccessRevision;

    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableTheory]
    [InlineData(ScanConfirmationTargetKind.Expense)]
    [InlineData(ScanConfirmationTargetKind.Payment)]
    [InlineData(ScanConfirmationTargetKind.WorkOrder)]
    [InlineData(ScanConfirmationTargetKind.Application)]
    [InlineData(ScanConfirmationTargetKind.Loan)]
    public async Task EachSupportedTarget_ConfirmsDraftAndPersistsCompleteTarget(
        ScanConfirmationTargetKind kind)
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(kind);

        var result = await UnitOfWork.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, $"writer-{kind}"),
            Command(draftId, kind),
            Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityId.Should().BePositive();
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId)).Status
            .Should().Be("Confirmed");
        (await TargetExistsAsync(verify.Db, kind, result.Value.TargetEntityId!.Value)).Should().BeTrue();
        if (kind == ScanConfirmationTargetKind.WorkOrder)
        {
            (await verify.Db.WorkOrderStatusEvents.CountAsync(row =>
                row.WorkOrderId == result.Value.TargetEntityId)).Should().Be(1);
        }
        if (kind == ScanConfirmationTargetKind.Expense)
        {
            (await verify.Db.ExpenseLineItems.CountAsync(row =>
                row.ExpenseId == result.Value.TargetEntityId)).Should().Be(1);
        }
    }

    [SkippableFact]
    public async Task ExpenseScanConfirmation_PersistsOperationalScopeMatchingTargetLocation()
    {
        SkipIfDockerUnavailable();

        await AssertExpenseScopeAsync(
            "portfolio-scope",
            null,
            null,
            null,
            ExpenseOperationalScope.Portfolio,
            expectedPropertyId: null,
            expectedUnitId: null,
            expectedWorkOrderId: null);
        await AssertExpenseScopeAsync(
            "property-scope",
            _propertyId,
            null,
            null,
            ExpenseOperationalScope.Property,
            expectedPropertyId: _propertyId,
            expectedUnitId: null,
            expectedWorkOrderId: null);
        await AssertExpenseScopeAsync(
            "unit-scope",
            null,
            _unitId,
            null,
            ExpenseOperationalScope.Unit,
            expectedPropertyId: _propertyId,
            expectedUnitId: _unitId,
            expectedWorkOrderId: null);
        await AssertExpenseScopeAsync(
            "work-order-scope",
            null,
            null,
            _workOrderId,
            ExpenseOperationalScope.WorkOrder,
            expectedPropertyId: _propertyId,
            expectedUnitId: _unitId,
            expectedWorkOrderId: _workOrderId);
    }

    [SkippableFact]
    public async Task DuplicateReceipt_ReplaysConcreteWriterResultWithoutSecondTarget()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var identity = ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, "duplicate-receipt");
        var uniqueVendor = $"Replay Vendor {draftId}";
        var command = Command(draftId, ScanConfirmationTargetKind.Expense) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Expense,
                Expense: new ScanExpenseTargetData(
                    Receipt(uniqueVendor), true, _propertyId, _unitId, null)),
        };

        var first = await UnitOfWork.ExecuteAsync(identity, command, Codec);
        var replay = await UnitOfWork.ExecuteAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.Expenses.CountAsync(row => row.Description == uniqueVendor))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task ExpenseScanConfirmation_MissingRequiredReviewFactsRejectsAndRollsBackClaimTargetAndReceipt()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "expense-missing-required-review-facts");
        var incompleteReceipt = Receipt(" ") with
        {
            TransactionDate = null,
            Subtotal = 0m,
            Total = null,
            Category = null,
        };
        var command = Command(draftId, ScanConfirmationTargetKind.Expense) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Expense,
                Expense: new ScanExpenseTargetData(
                    incompleteReceipt, true, null, null, null)),
        };
        int expenseCountBefore;
        await using (var before = Scope())
        {
            expenseCountBefore = await before.Db.Expenses.CountAsync(row => row.PortfolioId == _portfolioId);
        }

        var action = () => UnitOfWork.ExecuteAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*vendor*transaction date*positive total or subtotal*category*property, unit, or work order*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.Expenses.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(expenseCountBefore);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task SignedLeaseImport_PersistsCanonicalAgreementArtifactAndPartyGraph()
    {
        SkipIfDockerUnavailable();
        var source = await SeedLeaseDraftAsync("Zillow signed lease import");
        var target = LeaseTarget(
            _propertyId,
            _unitId,
            _tenantId,
            leaseManagementId: _leaseManagementId,
            tenantAccountId: _tenantAccountId);
        var outcome = await UnitOfWork.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, source.DraftId, "signed-zillow-import"),
            LeaseCommand(source, target),
            Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        outcome.Value.TargetEntityType.Should().Be(nameof(LeaseAgreement));
        await using var verify = Scope();
        var agreement = await verify.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.TargetEntityId);
        agreement.LeaseManagementId.Should().Be(_leaseManagementId);
        agreement.IssuedArtifactId.Should().NotBeNull();
        agreement.ExecutedArtifactId.Should().Be(agreement.IssuedArtifactId);
        agreement.FullyExecutedAtUtc.Should().NotBeNull();
        var artifact = await verify.Db.LegalDocumentArtifacts.AsNoTracking()
            .SingleAsync(row => row.Id == agreement.ExecutedArtifactId);
        artifact.ArtifactKind.Should().Be(LegalDocumentArtifactKind.ExecutedAgreement);
        artifact.StoredFileId.Should().Be(source.StoredFileId);
        artifact.ContentSha256.Should().Be(source.Sha256);
        (await verify.Db.LeaseManagementParties.CountAsync(row =>
            row.LeaseManagementId == _leaseManagementId)).Should().Be(1);
        (await verify.Db.LeaseAgreementSigners.CountAsync(row =>
            row.LeaseAgreementId == agreement.Id)).Should().Be(1);
    }

    [SkippableFact]
    public async Task EmptyPortfolioLeaseImport_BootstrapsOneCanonicalGraphAndReplaysReceipt()
    {
        SkipIfDockerUnavailable();
        var source = await SeedLeaseDraftAsync("Uploaded signed lease");
        var target = LeaseTarget(
            propertyId: 0,
            unitId: null,
            tenantId: null,
            propertyName: "Maple House",
            propertyAddress: "55 Maple Avenue",
            propertyCity: "Akron",
            propertyState: "OH",
            propertyPostalCode: "44308",
            rentalStructure: RentalStructure.SingleRental,
            unitNumber: null);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, source.DraftId, "empty-portfolio-bootstrap");
        var command = LeaseCommand(source, target);

        var first = await UnitOfWork.ExecuteAsync(identity, command, Codec);
        var replay = await UnitOfWork.ExecuteAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        var agreement = await verify.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(row => row.Id == first.Value.TargetEntityId);
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == agreement.LeaseManagementId);
        var property = await verify.Db.Properties.AsNoTracking()
            .SingleAsync(row => row.Id == relationship.PropertyId);
        property.AddressLine1.Should().Be("55 Maple Avenue");
        var unit = await verify.Db.Units.AsNoTracking()
            .SingleAsync(row => row.Id == relationship.UnitId);
        unit.UnitNumber.Should().Be("Property");
        (await verify.Db.TenantAccounts.CountAsync(row =>
            row.LeaseManagementId == relationship.Id)).Should().Be(1);
        (await verify.Db.LeaseManagementParties.CountAsync(row =>
            row.LeaseManagementId == relationship.Id)).Should().Be(1);
        (await verify.Db.LeaseAgreementSigners.CountAsync(row =>
            row.LeaseAgreementId == agreement.Id)).Should().Be(1);
        (await verify.Db.LegalDocumentArtifacts.CountAsync(row =>
            row.Id == agreement.ExecutedArtifactId
            && row.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement)).Should().Be(1);
        (await verify.Db.Properties.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.AddressLine1 == "55 Maple Avenue")).Should().Be(1);
    }

    [SkippableFact]
    public async Task HistoricalSignedLeaseImport_PersistsReviewedPossessionAndProjectsOccupied()
    {
        SkipIfDockerUnavailable();
        var source = await SeedLeaseDraftAsync("Historical signed lease");
        var possessionGivenAtUtc = CommandTime.AddMonths(-1);
        var target = LeaseTarget(
            propertyId: 0,
            unitId: null,
            tenantId: null,
            propertyName: "Possession House",
            propertyAddress: "71 Possession Avenue",
            propertyCity: "Akron",
            propertyState: "OH",
            propertyPostalCode: "44308",
            rentalStructure: RentalStructure.SingleRental,
            unitNumber: null,
            startDate: possessionGivenAtUtc,
            endDate: CommandTime.AddMonths(11),
            possessionGivenAtUtc: possessionGivenAtUtc);

        var outcome = await UnitOfWork.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, source.DraftId, "historical-possession"),
            LeaseCommand(source, target),
            Codec);

        await using var verify = Scope();
        var agreement = await verify.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.TargetEntityId);
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == agreement.LeaseManagementId);
        relationship.PossessionGivenAtUtc.Should().Be(possessionGivenAtUtc);
        var lifecycle = await verify.Db.LeaseManagementLifecycleProjections.AsNoTracking()
            .SingleAsync(row => row.LeaseManagementId == relationship.Id);
        lifecycle.Lifecycle.Should().Be("Occupied");
        lifecycle.HasGoverningAgreementWithoutPossession.Should().BeFalse();
        lifecycle.HasReconciliationException.Should().BeFalse();
    }

    [SkippableFact]
    public async Task HistoricalSignedLeaseImport_WithoutPossessionRejectsAndRollsBackWholeGraph()
    {
        SkipIfDockerUnavailable();
        var source = await SeedLeaseDraftAsync("Contradictory historical signed lease");
        var target = LeaseTarget(
            propertyId: 0,
            unitId: null,
            tenantId: null,
            propertyName: "Rollback House",
            propertyAddress: "72 Rollback Avenue",
            propertyCity: "Akron",
            propertyState: "OH",
            propertyPostalCode: "44308",
            rentalStructure: RentalStructure.SingleRental,
            unitNumber: null,
            startDate: CommandTime.AddMonths(-1),
            endDate: CommandTime.AddMonths(11));
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, source.DraftId, "historical-missing-possession");
        var command = LeaseCommand(source, target);

        var action = () => UnitOfWork.ExecuteAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*requires the reviewed possession date*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == source.DraftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.Properties.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.AddressLine1 == "72 Rollback Avenue"))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task ForeignPropertyReference_IsUnauthorizedAndRollsBackClaimTargetAndReceipt()
    {
        SkipIfDockerUnavailable();
        int foreignPropertyId;
        await using (var arrange = Scope())
        {
            var foreign = new Portfolio
            {
                Name = "Foreign",
                ManagementCompanyName = "Other Co",
                TimeZone = "UTC",
                CreatedAt = CommandTime,
                UpdatedAt = CommandTime,
            };
            arrange.Db.Portfolios.Add(foreign);
            await arrange.Db.SaveChangesAsync();
            var property = new Property
            {
                PortfolioId = foreign.Id,
                Name = "Foreign House",
                AddressLine1 = "99 Other St",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
                CreatedAt = CommandTime,
                UpdatedAt = CommandTime,
            };
            arrange.Db.Properties.Add(property);
            await arrange.Db.SaveChangesAsync();
            foreignPropertyId = property.Id;
        }
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, "foreign-property");
        int loanCountBefore;
        await using (var before = Scope())
        {
            loanCountBefore = await before.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId);
        }
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: LoanTarget(foreignPropertyId)),
        };

        var action = () => UnitOfWork.ExecuteAsync(identity, command, Codec);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId)).Status
            .Should().Be("Reviewing");
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(loanCountBefore);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    private ConfirmScanDraftCommand Command(int draftId, ScanConfirmationTargetKind kind) => new(
        _portfolioId,
        draftId,
        ConfirmedByUserId: _actorUserId,
        ConfirmedAtUtc: CommandTime,
        ExpectedDraftFingerprint: _preparedFingerprints[draftId],
        Target: kind switch
        {
            ScanConfirmationTargetKind.Expense => new ScanConfirmationTargetData(
                kind,
                Expense: new ScanExpenseTargetData(
                    Receipt("Fresh Vendor"), true, _propertyId, _unitId, null)),
            ScanConfirmationTargetKind.Payment => new ScanConfirmationTargetData(
                kind,
                Payment: new ScanPaymentTargetData(Receipt("Rent payer"), _tenantAccountId)),
            ScanConfirmationTargetKind.WorkOrder => new ScanConfirmationTargetData(
                kind,
                WorkOrder: new ScanWorkOrderTargetData(
                    _propertyId, _unitId, _tenantId, _leaseManagementId, _vendorId,
                    "Leaking sink", "Water under sink", "Plumbing",
                    WorkOrderPriority.High, 150m)),
            ScanConfirmationTargetKind.Application => new ScanConfirmationTargetData(
                kind,
                Application: new ScanApplicationTargetData(
                    "Jordan", "Applicant", "jordan@example.test", "555-0101", null,
                    "10 Current St", "ACME", 5_000m, CommandTime.AddMonths(1),
                    "Scan House Unit 1", "1234", "Guarantor", "Paper application",
                    _propertyId, _unitId)),
            ScanConfirmationTargetKind.Loan => new ScanConfirmationTargetData(
                kind,
                Loan: LoanTarget(_propertyId)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        },
        AuthSessionId: _authSessionId,
        AccessContextId: _accessContextId,
        ExpectedAccessRevision: _accessRevision,
        DeliveryIdempotencyKey: $"scan-confirm:{_portfolioId}:{draftId}:{kind}");

    private ConfirmScanDraftCommand LeaseCommand(
        LeaseDraftSource source,
        ScanLeaseTargetData target) => new(
        _portfolioId,
        source.DraftId,
        ConfirmedByUserId: _actorUserId,
        ConfirmedAtUtc: CommandTime,
        ExpectedDraftFingerprint: _preparedFingerprints[source.DraftId],
        Target: new ScanConfirmationTargetData(
            ScanConfirmationTargetKind.LeaseAgreement,
            LeaseAgreement: target),
        SourceStoredFileId: source.StoredFileId,
        AuthSessionId: _authSessionId,
        AccessContextId: _accessContextId,
        ExpectedAccessRevision: _accessRevision,
        DeliveryIdempotencyKey: $"scan-confirm:{_portfolioId}:{source.DraftId}:lease-agreement",
        SourceContentSha256: source.Sha256,
        SourceLabel: source.SourceLabel);

    private async Task AssertExpenseScopeAsync(
        string idempotencyKey,
        int? propertyId,
        int? unitId,
        int? workOrderId,
        ExpenseOperationalScope expectedScope,
        int? expectedPropertyId,
        int? expectedUnitId,
        int? expectedWorkOrderId)
    {
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var command = Command(draftId, ScanConfirmationTargetKind.Expense) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Expense,
                Expense: new ScanExpenseTargetData(
                    Receipt($"Expense {idempotencyKey}"), true, propertyId, unitId, workOrderId)),
            DeliveryIdempotencyKey = $"scan-confirm:{_portfolioId}:{draftId}:{idempotencyKey}",
        };

        var result = await UnitOfWork.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, idempotencyKey),
            command,
            Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var expense = await verify.Db.Expenses.AsNoTracking()
            .SingleAsync(row => row.Id == result.Value.TargetEntityId);
        expense.OperationalScope.Should().Be(expectedScope);
        expense.PropertyId.Should().Be(expectedPropertyId);
        expense.UnitId.Should().Be(expectedUnitId);
        expense.WorkOrderId.Should().Be(expectedWorkOrderId);
    }

    private static ScanLeaseTargetData LeaseTarget(
        int propertyId,
        int? unitId,
        int? tenantId,
        int? leaseManagementId = null,
        int? tenantAccountId = null,
        string? propertyName = null,
        string? propertyAddress = null,
        string? propertyCity = null,
        string? propertyState = null,
        string? propertyPostalCode = null,
        RentalStructure? rentalStructure = null,
        string? unitNumber = "1",
        DateTime? startDate = null,
        DateTime? endDate = null,
        DateTime? possessionGivenAtUtc = null) => new(
        propertyId,
        unitId,
        tenantId,
        TenantName: tenantId.HasValue ? null : "Jordan Tenant",
        TenantEmail: "jordan@example.test",
        TenantPhone: null,
        TenantEmergencyContact: null,
        PropertyName: propertyName,
        PropertyType: null,
        RentalStructure: rentalStructure,
        PropertyAddress: propertyAddress,
        PropertyCity: propertyCity,
        PropertyState: propertyState,
        PropertyPostalCode: propertyPostalCode,
        UnitNumber: unitNumber,
        UnitBedrooms: 2m,
        UnitBathrooms: 1m,
        UnitSquareFeet: 900,
        LeaseNumber: "EXT-LEASE-1",
        StartDate: startDate ?? new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate: endDate ?? new DateTime(2027, 7, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent: 1_250m,
        SecurityDeposit: 1_250m,
        LateFee: 50m,
        RentDueDay: 1,
        ReviewDisposition: LeaseScanReviewDisposition.AlreadyFullySigned,
        LeaseManagementId: leaseManagementId,
        TenantAccountId: tenantAccountId,
        TermsSchemaVersion: 1,
        TermsPayload: "{}",
        PossessionGivenAtUtc: possessionGivenAtUtc);

    private static ScanLoanTargetData LoanTarget(int propertyId) => new(
        propertyId,
        "Test Bank",
        200_000m,
        199_500m,
        6.125m,
        360,
        CommandTime.AddYears(-1),
        1,
        1_250m,
        300m,
        true,
        true,
        "Imported statement");

    private static ScanReceiptData Receipt(string vendorName) => new(
        vendorName, null, null, null, null, "R-1", CommandTime,
        120m, 5m, null, null, null, null, 125m, "Check", null,
        ScheduleECategory.Repairs, "Receipt", "Reviewed", null,
        [new ScanReceiptLineData("Part", 1m, 120m, 120m)],
        "Rent payer", "1001", "Test Bank", []);

    private async Task<int> SeedDraftAsync(ScanConfirmationTargetKind kind)
    {
        await using var scope = Scope();
        var path = $"scan/{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}.jpg";
        var source = new StoredFile
        {
            PortfolioId = _portfolioId,
            FileName = Path.GetFileName(path),
            FilePath = path,
            ContentType = "image/jpeg",
            FileSize = 100,
            EntityType = kind.ToString(),
            UploadedAt = CommandTime.AddMinutes(-5),
        };
        var draft = new ScanDraft
        {
            PortfolioId = _portfolioId,
            FilePath = path,
            SourceStoredFile = source,
            TargetEntityType = kind.ToString(),
            Status = "Reviewing",
            ExtractedFields = "{\"source\":\"integration-test\"}",
            CreatedAt = CommandTime.AddMinutes(-5),
        };
        scope.Db.AddRange(source, draft);
        await scope.Db.SaveChangesAsync();
        _preparedFingerprints[draft.Id] = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType, draft.SourceStoredFileId, draft.ExtractedFields);
        return draft.Id;
    }

    private async Task<LeaseDraftSource> SeedLeaseDraftAsync(string sourceLabel)
    {
        await using var scope = Scope();
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"lease:{Guid.NewGuid():N}")));
        var path = $"scan/lease-{Guid.NewGuid():N}.pdf";
        var source = new StoredFile
        {
            PortfolioId = _portfolioId,
            FileName = Path.GetFileName(path),
            FilePath = path,
            ContentType = "application/pdf",
            FileSize = 1_024,
            EntityType = nameof(ScanDraft),
            UploadedAt = CommandTime.AddMinutes(-5),
        };
        var draft = new ScanDraft
        {
            PortfolioId = _portfolioId,
            FilePath = path,
            SourceStoredFile = source,
            SourceContentSha256 = sha256,
            SourceLabel = sourceLabel,
            TargetEntityType = nameof(LeaseAgreement),
            Status = "Reviewing",
            ExtractedFields = "{}",
            CaptureAccessContextId = _accessContextId,
            CaptureAccessRevision = _accessRevision,
            CreatedAt = CommandTime.AddMinutes(-5),
        };
        scope.Db.AddRange(source, draft);
        await scope.Db.SaveChangesAsync();
        _preparedFingerprints[draft.Id] = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256,
            draft.CaptureAccessContextId,
            draft.CaptureAccessRevision,
            sourceLabel: draft.SourceLabel);
        return new LeaseDraftSource(draft.Id, source.Id, sha256, sourceLabel);
    }

    private static Task<bool> TargetExistsAsync(
        RentalCommandDbContext db,
        ScanConfirmationTargetKind kind,
        int id) => kind switch
        {
            ScanConfirmationTargetKind.Expense => db.Expenses.AnyAsync(row => row.Id == id),
            ScanConfirmationTargetKind.Payment => db.TenantLedgerEntries.AnyAsync(row =>
                row.TenantAccountId == id
                && row.EntryType == TenantLedgerEntryType.PaymentReceipt),
            ScanConfirmationTargetKind.WorkOrder => db.WorkOrders.AnyAsync(row => row.Id == id),
            ScanConfirmationTargetKind.Application => db.RentalApplications.AnyAsync(row => row.Id == id),
            ScanConfirmationTargetKind.Loan => db.Loans.AnyAsync(row => row.Id == id),
            _ => Task.FromResult(false),
        };

    private IAtomicUnitOfWork UnitOfWork =>
        (_services ?? throw new InvalidOperationException()).GetRequiredService<IAtomicUnitOfWork>();

    private TestScope Scope() => TestScope.Create(_services ?? throw new InvalidOperationException());

    private void SkipIfDockerUnavailable() =>
        Skip.IfNot(_dockerAvailable, "Docker socket preflight failed; PostgreSQL scan tests skipped.");

    private static async Task<bool> DockerSocketPreflightAsync()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (Uri.TryCreate(dockerHost, UriKind.Absolute, out var hostUri)
            && hostUri.Scheme is "tcp" or "http" or "https")
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(hostUri.Host, hostUri.Port > 0 ? hostUri.Port : 2375);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        var candidates = new[]
        {
            dockerHost?.StartsWith("unix://", StringComparison.Ordinal) == true ? dockerHost[7..] : null,
            "/var/run/docker.sock",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima/default/docker.sock"),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path!));
                return true;
            }
            catch (SocketException)
            {
                // Try the next well-known socket.
            }
        }
        return false;
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:production-scan-writer";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record LeaseDraftSource(
        int DraftId,
        int StoredFileId,
        string Sha256,
        string SourceLabel);

    private sealed class TestScope(AsyncServiceScope scope, RentalCommandDbContext db) : IAsyncDisposable
    {
        public RentalCommandDbContext Db { get; } = db;
        public static TestScope Create(IServiceProvider services)
        {
            var scope = services.CreateAsyncScope();
            return new TestScope(scope, scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        }
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}

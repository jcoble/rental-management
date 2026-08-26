using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Scanning;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL coverage for the production scan target writers.</summary>
public sealed class ProductionScanConfirmationTargetWriterTests : IAsyncLifetime
{
    private static readonly DateTime CommandTime =
        new(2026, 7, 11, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InterceptorSaveTime =
        new(2031, 3, 17, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime StatementDate =
        new(2027, 1, 20, 0, 0, 0, DateTimeKind.Utc);
    private const decimal StatementOpeningBalance = 125_825m;
    private const decimal StatementPrincipal = 431m;
    private const decimal StatementInterest = 623m;
    private const decimal StatementPrincipalInterest = 1_054m;
    private const decimal StatementEscrow = 318m;
    private const decimal StatementTotal = 1_372m;
    private const decimal StatementBalanceAfter = 125_394m;
    private static readonly AtomicJsonResultCodec<ConfirmScanDraftResult> Codec =
        new("scan-confirm.result.v1");
    private static readonly AtomicJsonResultCodec<TenantPaymentRefundResult> RefundCodec =
        new("tenant-account.payment.refund.v1");

    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _propertyId;
    private int _unitId;
    private int _tenantId;
    private int _leaseManagementId;
    private int _leaseAgreementId;
    private int _tenantAccountId;
    private int _vendorId;
    private int _workOrderId;
    private int _ownerEntityId;
    private int _actorUserId;
    private Guid _authSessionId;
    private int _accessContextId;
    private long _accessRevision;
    private readonly Dictionary<int, string> _preparedFingerprints = [];

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable) return;

        _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Model);
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(InterceptorSaveTime));
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddScoped<IScanConfirmationTargetWriter, ProductionScanConfirmationTargetWriter>();
        services.AddAtomicPersistenceKernel();
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
        await scope.Db.Database.ExecuteSqlRawAsync(RelationshipAccessProjectionSql.Create);
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
        await new ChartOfAccountsSeedService(scope.Db).SeedAsync(_portfolioId);
        await scope.Db.SaveChangesAsync();

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
        var owner = new OwnerEntity
        {
            PortfolioId = _portfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Baseline Owner",
            Email = "baseline-owner@example.test",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        scope.Db.AddRange(property, tenant, vendor, owner);
        await scope.Db.SaveChangesAsync();
        _propertyId = property.Id;
        _tenantId = tenant.Id;
        _vendorId = vendor.Id;
        _ownerEntityId = owner.Id;

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
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = _portfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-SCAN-BASE",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(CommandTime.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(CommandTime.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(CommandTime.AddMonths(-1)),
            BaseRentAmount = 125m,
            RentDueDay = 1,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 0m,
            GracePeriodDays = 0,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                _portfolioId, _actorUserId, CommandTime),
            CreatedAtUtc = CommandTime,
            UpdatedAtUtc = CommandTime,
            CreatedByUserId = _actorUserId,
        };
        scope.Db.AddRange(tenantAccount, primaryParty, agreement);
        await scope.Db.SaveChangesAsync();
        _tenantAccountId = tenantAccount.Id;
        _leaseManagementId = relationship.Id;
        _leaseAgreementId = agreement.Id;

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

    [SkippableFact]
    public async Task GuidedSetupManualLease_UsesCanonicalScanConfirmationCascade()
    {
        SkipIfDockerUnavailable();

        var target = new ScanLeaseTargetData(
            PropertyId: 0,
            UnitId: null,
            TenantId: null,
            TenantName: "Manual Guided Tenant",
            TenantEmail: "manual-guided-tenant@example.test",
            TenantPhone: "555-0199",
            TenantEmergencyContact: null,
            PropertyName: "Manual Guided House",
            PropertyType: "SingleFamily",
            RentalStructure: RentalStructure.SingleRental,
            PropertyAddress: "834 Manual Lane",
            PropertyCity: "Akron",
            PropertyState: "OH",
            PropertyPostalCode: "44308",
            UnitNumber: "1",
            UnitBedrooms: 2m,
            UnitBathrooms: 1m,
            UnitSquareFeet: 900,
            LeaseNumber: "MANUAL-GUIDED-834",
            StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate: new DateTime(2027, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent: 1_450m,
            SecurityDeposit: 1_450m,
            LateFee: 75m,
            RentDueDay: 1,
            ReviewDisposition: LeaseScanReviewDisposition.NeedsSignatures,
            TermsPayload: "{}",
            RentTrackingStartMode: RentTrackingStartMode.ForwardOnly);
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            target,
            "guided-setup-manual-lease:test-834");

        var outcome = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "guided-setup.manual-lease",
                $"{_portfolioId}:guided-setup-manual-lease:test-834"),
            command,
            Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        outcome.Value.LeaseManagementId.Should().BePositive();
        outcome.Value.TargetEntityId.Should().BePositive();

        var replay = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "guided-setup.manual-lease",
                $"{_portfolioId}:guided-setup-manual-lease:test-834"),
            command,
            Codec);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(outcome.Value);

        await using var verify = Scope();
        var agreement = await verify.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.TargetEntityId);
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.LeaseManagementId);
        var account = await verify.Db.TenantAccounts.AsNoTracking()
            .SingleAsync(row => row.LeaseManagementId == relationship.Id);
        var party = await verify.Db.LeaseManagementParties.AsNoTracking()
            .SingleAsync(row => row.LeaseManagementId == relationship.Id);
        var unit = await verify.Db.Units.AsNoTracking()
            .SingleAsync(row => row.Id == relationship.UnitId);
        var property = await verify.Db.Properties.AsNoTracking()
            .SingleAsync(row => row.Id == relationship.PropertyId);
        var tenant = await verify.Db.Tenants.AsNoTracking()
            .SingleAsync(row => row.Id == party.TenantId);

        property.Name.Should().Be("Manual Guided House");
        unit.UnitNumber.Should().Be("1");
        tenant.Email.Should().Be("manual-guided-tenant@example.test");
        account.LeaseManagementId.Should().Be(relationship.Id);
        party.TenantId.Should().Be(tenant.Id);
        agreement.LeaseManagementId.Should().Be(relationship.Id);
    }

    [SkippableFact]
    public async Task GuidedSetupManualLease_UsesInterceptorSaveTime_NotCommandBusinessTime()
    {
        SkipIfDockerUnavailable();
        var marker = $"manual-clock-{Guid.NewGuid():N}";
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            GuidedSetupManualTarget(marker),
            $"guided-setup-manual-lease:{marker}");
        var identity = new AtomicCommandIdentity(
            "guided-setup.manual-lease", $"{_portfolioId}:{marker}");

        DateTime databaseBusinessClock;
        await using (var before = Scope())
        {
            databaseBusinessClock = await before.Db.Database
                .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync();
        }

        await ExecuteAtomicAsync(identity, command, Codec);

        await using var verify = Scope();
        var trackedAuditTimestamps = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType != nameof(Unit))
            .Select(row => row.Timestamp)
            .ToArrayAsync();
        trackedAuditTimestamps.Should().NotBeEmpty();
        trackedAuditTimestamps.Should().OnlyContain(timestamp => timestamp == InterceptorSaveTime);
        trackedAuditTimestamps.Should().OnlyContain(timestamp => timestamp != databaseBusinessClock);
    }

    [SkippableFact]
    public async Task GuidedSetupManualLease_ReplayAuthorization_AcquiresLegacyScopeLocksInOrder()
    {
        SkipIfDockerUnavailable();
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            GuidedSetupManualTarget($"manual-lock-{Guid.NewGuid():N}"),
            $"guided-setup-manual-lease:lock:{Guid.NewGuid():N}");
        var acquired = new List<string>();
        var context = new Mock<IAtomicCommandContext>();
        context.SetupGet(item => item.BusinessNowUtc).Returns(CommandTime);
        context.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((lockNamespace, id, _) =>
                acquired.Add($"{lockNamespace}:{id}"))
            .Returns(Task.CompletedTask);
        context.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((lockNamespace, id, _) =>
                acquired.Add($"{lockNamespace}:{id}"))
            .Returns(Task.CompletedTask);

        await using var scope = Scope();
        await CreateManualLeaseRule.AuthorizeAsync(
            scope.Db, command, context.Object, CancellationToken.None);

        acquired.Should().Equal(
            $"AuthSession:{_authSessionId}",
            $"WorkspaceAccessContext:{_accessContextId}",
            $"Portfolio:{_portfolioId}");
    }

    [SkippableTheory]
    [InlineData("Storage")]
    [InlineData("Parking")]
    [InlineData("Commercial")]
    public async Task GuidedSetupManualLease_AllowsMissingBedsAndBathsForNonResidentialHomes(
        string propertyType)
    {
        SkipIfDockerUnavailable();

        var marker = $"nr-{propertyType[..3].ToLowerInvariant()}-{Guid.NewGuid():N}";
        var target = GuidedSetupManualTarget(marker) with
        {
            PropertyType = propertyType,
            UnitBedrooms = null,
            UnitBathrooms = null,
        };
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            target,
            $"guided-setup-manual-lease:nonres:{marker}");

        var outcome = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "guided-setup.manual-lease",
                $"{_portfolioId}:nonres:{marker}"),
            command,
            Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.LeaseManagementId);
        var unit = await verify.Db.Units.AsNoTracking()
            .SingleAsync(row => row.Id == relationship.UnitId);
        unit.Bedrooms.Should().Be(0m);
        unit.Bathrooms.Should().Be(0m);
    }

    [SkippableTheory]
    [InlineData("lease-number-blank")]
    [InlineData("lease-number-too-long")]
    [InlineData("start-date-missing")]
    [InlineData("end-date-missing")]
    [InlineData("end-before-start")]
    [InlineData("monthly-rent-not-positive")]
    [InlineData("rent-due-day-out-of-range")]
    [InlineData("property-id-not-positive")]
    [InlineData("unit-id-not-positive")]
    [InlineData("tenant-id-not-positive")]
    [InlineData("security-deposit-negative")]
    [InlineData("late-fee-negative")]
    [InlineData("bedrooms-missing")]
    [InlineData("bathrooms-missing")]
    [InlineData("bedrooms-negative")]
    [InlineData("bathrooms-negative")]
    [InlineData("square-feet-negative")]
    public async Task GuidedSetupManualLease_InvalidInput_IsRejectedBeforeAnyBusinessFlush(
        string invalidClass)
    {
        SkipIfDockerUnavailable();

        var marker = $"m834-{Guid.NewGuid():N}";
        var target = GuidedSetupManualTarget(marker);
        target = invalidClass switch
        {
            "lease-number-blank" => target with { LeaseNumber = "   " },
            "lease-number-too-long" => target with { LeaseNumber = new string('L', 101) },
            "start-date-missing" => target with { StartDate = null },
            "end-date-missing" => target with { EndDate = null },
            "end-before-start" => target with { EndDate = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc) },
            "monthly-rent-not-positive" => target with { MonthlyRent = 0m },
            "rent-due-day-out-of-range" => target with { RentDueDay = 0 },
            "property-id-not-positive" => target with { PropertyId = -1 },
            "unit-id-not-positive" => target with { UnitId = -1 },
            "tenant-id-not-positive" => target with { TenantId = -1 },
            "security-deposit-negative" => target with { SecurityDeposit = -1m },
            "late-fee-negative" => target with { LateFee = -1m },
            "bedrooms-missing" => target with { UnitBedrooms = null },
            "bathrooms-missing" => target with { UnitBathrooms = null },
            "bedrooms-negative" => target with { UnitBedrooms = -1m },
            "bathrooms-negative" => target with { UnitBathrooms = -1m },
            "square-feet-negative" => target with { UnitSquareFeet = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidClass), invalidClass, null),
        };
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            target,
            $"guided-setup-manual-lease:invalid:{marker}");
        var identity = new AtomicCommandIdentity(
            "guided-setup.manual-lease",
            $"{_portfolioId}:invalid:{marker}");

        var before = await ReadManualLeasePersistenceCountsAsync(marker, target.LeaseNumber, identity);
        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>();

        var after = await ReadManualLeasePersistenceCountsAsync(marker, target.LeaseNumber, identity);
        after.Should().Be(before);
    }

    [SkippableFact]
    public async Task GuidedSetupManualLease_CanonicalCascadeFailureAfterDraftFlush_RollsBackAllRows()
    {
        SkipIfDockerUnavailable();

        var marker = $"m834-fail-{Guid.NewGuid():N}";
        var target = GuidedSetupManualTarget(marker);
        var command = new CreateManualLeaseCommand(
            _portfolioId,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            target,
            $"guided-setup-manual-lease:failure:{marker}");
        var identity = new AtomicCommandIdentity(
            "guided-setup.manual-lease",
            $"{_portfolioId}:failure:{marker}");

        var before = await ReadManualLeasePersistenceCountsAsync(
            marker, target.LeaseNumber, identity);
        await InstallManualLeaseCascadeFailureTriggerAsync();
        try
        {
            var action = () => ExecuteAtomicAsync(identity, command, Codec);

            await action.Should().ThrowAsync<Exception>()
                .Where(ex => ex.ToString().Contains(
                    "fail manual lease canonical cascade after draft flush",
                    StringComparison.Ordinal));
        }
        finally
        {
            await RemoveManualLeaseCascadeFailureTriggerAsync();
        }

        var after = await ReadManualLeasePersistenceCountsAsync(
            marker, target.LeaseNumber, identity);
        after.Drafts.Should().Be(before.Drafts);
        after.Properties.Should().Be(before.Properties);
        after.Units.Should().Be(before.Units);
        after.Tenants.Should().Be(before.Tenants);
        after.LeaseManagements.Should().Be(before.LeaseManagements);
        after.TenantAccounts.Should().Be(before.TenantAccounts);
        after.LeaseManagementParties.Should().Be(before.LeaseManagementParties);
        after.Agreements.Should().Be(before.Agreements);
        after.Receipts.Should().Be(before.Receipts);
        after.AuditRows.Should().Be(before.AuditRows);
        after.OutboxRows.Should().Be(before.OutboxRows);
    }

    [SkippableTheory]
    [InlineData(ScanConfirmationTargetKind.Expense)]
    [InlineData(ScanConfirmationTargetKind.Payment)]
    [InlineData(ScanConfirmationTargetKind.WorkOrder)]
    [InlineData(ScanConfirmationTargetKind.Application)]
    [InlineData(ScanConfirmationTargetKind.Loan)]
    [InlineData(ScanConfirmationTargetKind.PropertyAcquisition)]
    [InlineData(ScanConfirmationTargetKind.LeaseEndingNotice)]
    public async Task EachSupportedTarget_ConfirmsDraftAndPersistsCompleteTarget(
        ScanConfirmationTargetKind kind)
    {
        SkipIfDockerUnavailable();
        if (kind == ScanConfirmationTargetKind.LeaseEndingNotice)
        {
            await MarkBaseRelationshipOccupiedAsync();
        }
        var draftId = await SeedDraftAsync(kind);
        var command = Command(draftId, kind);
        if (kind is ScanConfirmationTargetKind.PropertyAcquisition or ScanConfirmationTargetKind.LeaseEndingNotice)
        {
            command = command with { SourceStoredFileId = await SourceStoredFileIdAsync(draftId) };
        }

        var result = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, $"writer-{kind}"),
            command,
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
    public async Task PaidExpenseScanConfirmation_PreservesReviewedDueDateAndPaidState()
    {
        SkipIfDockerUnavailable();
        var dueDate = new DateTime(2027, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "paid-expense-preserves-due-date");
        var command = Command(draftId, ScanConfirmationTargetKind.Expense);
        command = command with
        {
            Target = command.Target with
            {
                Expense = command.Target.Expense! with
                {
                    Receipt = command.Target.Expense.Receipt with { DueDate = dueDate },
                    IsPaid = true,
                },
            },
        };

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var expense = await verify.Db.Expenses.AsNoTracking()
            .SingleAsync(row => row.Id == result.Value.TargetEntityId);
        expense.DueDate.Should().Be(dueDate);
        expense.Status.Should().Be(ExpenseStatus.Paid);
        expense.PaidAt.Should().Be(CommandTime);
        expense.ReceiptData.Should().Contain("\"dueDate\": \"2027-03-02T00:00:00Z\"");
    }

    [SkippableFact]
    public async Task PaidExpenseScanConfirmation_AuditFailureRollsBackCompleteConfirmation()
    {
        SkipIfDockerUnavailable();
        var dueDate = new DateTime(2027, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var vendorName = $"Paid expense rollback {Guid.NewGuid():N}";
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "paid-expense-audit-failure");
        var command = Command(draftId, ScanConfirmationTargetKind.Expense);
        command = command with
        {
            Target = command.Target with
            {
                Expense = command.Target.Expense! with
                {
                    Receipt = command.Target.Expense.Receipt with
                    {
                        VendorName = vendorName,
                        DueDate = dueDate,
                    },
                    IsPaid = true,
                },
            },
        };
        int auditCountBefore;
        int outboxCountBefore;
        await using (var before = Scope())
        {
            auditCountBefore = await before.Db.AtomicAuditLogs.CountAsync();
            outboxCountBefore = await before.Db.OutboxMessages.CountAsync();
            await before.Db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION fail_paid_expense_scan_audit()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW."EntityType" = 'Expense' THEN
                        RAISE EXCEPTION 'fail paid expense scan audit';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_paid_expense_scan_audit
                BEFORE INSERT ON "AtomicAuditLogs"
                FOR EACH ROW EXECUTE FUNCTION fail_paid_expense_scan_audit();
                """);
        }

        try
        {
            var action = () => ExecuteAtomicAsync(identity, command, Codec);
            var failure = await action.Should().ThrowAsync<DbUpdateException>();
            failure.WithInnerException<PostgresException>()
                .WithMessage("*fail paid expense scan audit*");
        }
        finally
        {
            await using var cleanup = Scope();
            await cleanup.Db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_paid_expense_scan_audit ON "AtomicAuditLogs";
                DROP FUNCTION IF EXISTS fail_paid_expense_scan_audit();
                """);
        }

        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityType.Should().Be(nameof(ScanConfirmationTargetKind.Expense));
        source.EntityId.Should().BeNull();
        (await verify.Db.Expenses.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.Description == vendorName)).Should().Be(0);
        (await verify.Db.ExpenseLineItems.CountAsync(row =>
            row.Expense!.PortfolioId == _portfolioId
            && row.Expense.Description == vendorName)).Should().Be(0);
        (await verify.Db.ExpenseAllocations.CountAsync(row =>
            row.Expense!.PortfolioId == _portfolioId
            && row.Expense.Description == vendorName)).Should().Be(0);
        (await verify.Db.AtomicAuditLogs.CountAsync()).Should().Be(auditCountBefore);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.Db.OutboxMessages.CountAsync()).Should().Be(outboxCountBefore);
    }

    [SkippableFact]
    public async Task PaymentScanConfirmation_WithoutExplicitLedgerTarget_AllocatesAcrossOldestOpenCharges()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Payment);
        var rentChargeId = await SeedOpenTenantChargeAsync(
            75m, TenantLedgerEntryType.RentCharge, dueOnOffsetDays: 0);
        var lateFeeChargeId = await SeedOpenTenantChargeAsync(
            50m, TenantLedgerEntryType.LateFeeCharge, dueOnOffsetDays: 5);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "payment-open-charge-target");
        var command = Command(draftId, ScanConfirmationTargetKind.Payment);

        var result = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.LedgerEntryId.Should().BePositive();
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(result.Value);
        await using var verify = Scope();
        var receipt = await verify.Db.TenantLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.Id == result.Value.LedgerEntryId);
        receipt.ProviderPaymentAttemptId.Should().NotBeNull();
        var attempt = await verify.Db.TenantPaymentAttempts.AsNoTracking()
            .SingleAsync(row => row.Id == receipt.ProviderPaymentAttemptId);
        attempt.AttemptType.Should().Be(TenantPaymentAttemptType.UnappliedReceipt);
        attempt.ChargeLedgerEntryId.Should().BeNull();
        var allocations = await verify.Db.TenantLedgerAllocations.AsNoTracking()
            .Where(row => row.CreditEntryId == result.Value.LedgerEntryId)
            .OrderBy(row => row.DebitEntryId)
            .Select(row => new { row.DebitEntryId, row.Amount })
            .ToListAsync();
        allocations.Should().BeEquivalentTo(
        [
            new { DebitEntryId = rentChargeId, Amount = 75m },
            new { DebitEntryId = lateFeeChargeId, Amount = 50m },
        ]);
        (await verify.Db.TenantLedgerEntries.CountAsync(row =>
            row.BusinessKey == $"scan-receipt:{draftId}")).Should().Be(1);
        (await verify.Db.TenantPaymentAttempts.CountAsync(row =>
            row.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(1);

        var failedDraftId = await SeedDraftAsync(ScanConfirmationTargetKind.Payment);
        await SeedOpenTenantChargeAsync(
            75m, TenantLedgerEntryType.RentCharge, dueOnOffsetDays: 10);
        await SeedOpenTenantChargeAsync(
            50m, TenantLedgerEntryType.LateFeeCharge, dueOnOffsetDays: 15);
        var failedIdentity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, failedDraftId, "payment-allocation-failure");
        var failedCommand = Command(failedDraftId, ScanConfirmationTargetKind.Payment);
        failedCommand = failedCommand with
        {
            Target = failedCommand.Target with
            {
                Payment = failedCommand.Target.Payment! with
                {
                    Receipt = failedCommand.Target.Payment.Receipt with { CheckNumber = "1002" },
                },
            },
        };
        await using (var arrange = Scope())
        {
            await arrange.Db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION fail_scan_receipt_allocation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'fail scan receipt allocation';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_scan_receipt_allocation
                BEFORE INSERT ON "TenantLedgerAllocations"
                FOR EACH ROW EXECUTE FUNCTION fail_scan_receipt_allocation();
                """);
        }

        try
        {
            var action = () => ExecuteAtomicAsync(failedIdentity, failedCommand, Codec);
            await action.Should().ThrowAsync<PostgresException>()
                .WithMessage("*fail scan receipt allocation*");
        }
        finally
        {
            await using var cleanup = Scope();
            await cleanup.Db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_scan_receipt_allocation ON "TenantLedgerAllocations";
                DROP FUNCTION IF EXISTS fail_scan_receipt_allocation();
                """);
        }

        await using var rolledBack = Scope();
        (await rolledBack.Db.ScanDrafts.AsNoTracking()
            .SingleAsync(row => row.Id == failedDraftId)).Status.Should().Be("Reviewing");
        (await rolledBack.Db.TenantLedgerEntries.CountAsync(row =>
            row.BusinessKey == $"scan-receipt:{failedDraftId}")).Should().Be(0);
        (await rolledBack.Db.TenantPaymentAttempts.CountAsync(row =>
            row.IdempotencyKey == failedCommand.DeliveryIdempotencyKey)).Should().Be(0);
        (await rolledBack.Db.TenantLedgerAllocations.CountAsync(row =>
            row.BusinessKey.StartsWith($"scan-receipt:{failedDraftId}:"))).Should().Be(0);
        (await rolledBack.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == failedIdentity.CommandType
            && row.IdempotencyKey == failedIdentity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task RefundedPaymentScan_AllowsCorrectedDraftWithSameSourceToConfirmOnce()
    {
        SkipIfDockerUnavailable();
        const string sourceHash =
            "d250ed3f24f364bfce871e3da503c6560e5959fd9244785fe7b11de1cdee39af";
        var chargeId = await SeedOpenTenantChargeAsync(125m);
        var originalDraftId = await SeedDraftAsync(
            ScanConfirmationTargetKind.Payment, sourceHash, draftHashOnly: true);
        var original = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, originalDraftId, "payment-original"),
            Command(originalDraftId, ScanConfirmationTargetKind.Payment),
            Codec);
        var refundCommand = new RefundTenantPaymentCommand(
            _portfolioId,
            _tenantAccountId,
            original.Value.LedgerEntryId!.Value,
            DateOnly.FromDateTime(CommandTime),
            "Correct misclassified scanned payment",
            "Check",
            "refund-corrected-source",
            null,
            _actorUserId,
            _authSessionId,
            _accessContextId,
            _accessRevision,
            CapabilityKeys.MoneyPaymentsManage,
            "tenant-payment-refund:corrected-source",
            $"tenant-payment-refund:{_portfolioId}:{_tenantAccountId}:corrected-source");
        var refund = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.payment.refund",
                refundCommand.DeliveryIdempotencyKey),
            refundCommand,
            RefundCodec);
        refund.Value.Outcome.Should().Be(TenantPaymentRefundOutcome.Refunded);

        var correctedDraftId = await SeedDraftAsync(
            ScanConfirmationTargetKind.Payment, sourceHash, draftHashOnly: true);
        var corrected = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, correctedDraftId, "payment-corrected"),
            Command(correctedDraftId, ScanConfirmationTargetKind.Payment),
            Codec);

        corrected.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        corrected.Value.LedgerEntryId.Should().NotBe(original.Value.LedgerEntryId);
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.CountAsync(draft =>
            draft.PortfolioId == _portfolioId
            && draft.TargetEntityType == nameof(ScanConfirmationTargetKind.Payment)
            && draft.SourceContentSha256 == sourceHash
            && draft.Status == "Confirmed")).Should().Be(2);
        var correctedAllocation = await verify.Db.TenantLedgerAllocations.AsNoTracking()
            .SingleAsync(row => row.CreditEntryId == corrected.Value.LedgerEntryId);
        correctedAllocation.DebitEntryId.Should().Be(chargeId);
        correctedAllocation.Amount.Should().Be(125m);
    }

    [SkippableFact]
    public async Task WorkOrderScanConfirmation_PersistsReviewedAccessPacketWithAuditAndOutbox()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.WorkOrder);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "work-order-access-packet-confirm");
        var command = Command(draftId, ScanConfirmationTargetKind.WorkOrder) with
        {
            SourceStoredFileId = await SourceStoredFileIdAsync(draftId),
        };

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var workOrder = await verify.Db.WorkOrders.AsNoTracking()
            .SingleAsync(row => row.Id == result.Value.TargetEntityId);
        workOrder.RequesterName.Should().Be("Morgan Resident");
        workOrder.RequesterPhone.Should().Be("555-0134");
        workOrder.RequesterEmail.Should().Be("morgan@example.test");
        workOrder.ResidentMustBePresent.Should().BeTrue();
        workOrder.CallBeforeEntry.Should().BeTrue();
        workOrder.CallIfNotHome.Should().BeTrue();
        workOrder.PermissionToEnter.Should().BeTrue();
        workOrder.EntryNotes.Should().Be("Preferred window Tuesday 10 AM to noon; key under lockbox.");
        workOrder.PetWarnings.Should().Be("Dog in crate in bedroom.");
        workOrder.AccessWarnings.Should().Be("Use side gate; front steps are loose.");
        workOrder.TechnicianAccessInstructions.Should()
            .Be("Call Morgan before entry, use side gate, and keep the dog crated.");
        (await verify.Db.WorkOrderStatusEvents.CountAsync(row =>
            row.WorkOrderId == result.Value.TargetEntityId))
            .Should().Be(1);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == result.AttemptId
            && row.EntityType == nameof(WorkOrder)
            && row.EntityId == result.Value.TargetEntityId
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == $"scan-work-order-create:{command.DeliveryIdempotencyKey}"))
            .Should().Be(1);
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == command.SourceStoredFileId!.Value);
        source.EntityType.Should().Be(nameof(WorkOrder));
        source.EntityId.Should().Be(result.Value.TargetEntityId);
    }

    [SkippableFact]
    public async Task WorkOrderScanConfirmation_ReplayKeepsAccessPacketAuditAndOutboxExact()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.WorkOrder);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "work-order-access-packet-replay");
        var command = Command(draftId, ScanConfirmationTargetKind.WorkOrder);

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        var workOrder = await verify.Db.WorkOrders.AsNoTracking()
            .SingleAsync(row => row.Id == first.Value.TargetEntityId);
        workOrder.RequesterName.Should().Be("Morgan Resident");
        workOrder.RequesterPhone.Should().Be("555-0134");
        workOrder.RequesterEmail.Should().Be("morgan@example.test");
        workOrder.TechnicianAccessInstructions.Should()
            .Be("Call Morgan before entry, use side gate, and keep the dog crated.");
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(WorkOrder)
            && row.EntityId == first.Value.TargetEntityId
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == $"scan-work-order-create:{command.DeliveryIdempotencyKey}"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task WorkOrderScanConfirmation_InvalidPropertyRollsBackPacketDraftAuditReceiptAndOutbox()
    {
        SkipIfDockerUnavailable();
        int foreignPropertyId;
        await using (var arrange = Scope())
        {
            var foreign = new Portfolio
            {
                Name = "Foreign work-order access",
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
                Name = "Foreign Access House",
                AddressLine1 = "404 Other St",
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
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.WorkOrder);
        int workOrderCountBefore;
        int workOrderAuditCountBefore;
        await using (var before = Scope())
        {
            workOrderCountBefore = await before.Db.WorkOrders.CountAsync(row =>
                row.PortfolioId == _portfolioId);
            workOrderAuditCountBefore = await before.Db.AtomicAuditLogs.CountAsync(row =>
                row.EntityType == nameof(WorkOrder));
        }
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "work-order-access-packet-foreign-property");
        var command = Command(draftId, ScanConfirmationTargetKind.WorkOrder) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.WorkOrder,
                WorkOrder: WorkOrderTargetWithAccessPacket(foreignPropertyId, unitId: null, leaseManagementId: null)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.WorkOrders.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(workOrderCountBefore);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(WorkOrder)))
            .Should().Be(workOrderAuditCountBefore);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == $"scan-work-order-create:{command.DeliveryIdempotencyKey}"))
            .Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(ScanConfirmationTargetKind.Expense, nameof(Expense))]
    [InlineData(ScanConfirmationTargetKind.WorkOrder, nameof(WorkOrder))]
    public async Task ScanConfirmation_RecordsOneBusinessTargetAuditAndKeepsScanAudit(
        ScanConfirmationTargetKind kind,
        string targetEntityType)
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(kind);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, $"single-target-audit-{kind}");
        var command = Command(draftId, kind) with { SourceStoredFileId = sourceStoredFileId };

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var attemptId = result.AttemptId;
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == attemptId
            && row.EntityType == targetEntityType
            && row.EntityId == result.Value.TargetEntityId
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == attemptId
            && row.EntityType == nameof(ScanDraft)
            && row.EntityId == draftId
            && row.Operation == AuditLogOperation.Updated))
            .Should().Be(2);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == attemptId
            && row.EntityType == nameof(StoredFile)
            && row.Operation == AuditLogOperation.Updated))
            .Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.AttemptId == attemptId
            && row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task LeaseEndingNoticeScanConfirmation_MovesRelationshipLinksSourceAndDoesNotCreateAgreement()
    {
        SkipIfDockerUnavailable();
        await MarkBaseRelationshipOccupiedAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.LeaseEndingNotice);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "lease-ending-notice-confirm");
        var command = Command(draftId, ScanConfirmationTargetKind.LeaseEndingNotice) with
        {
            SourceStoredFileId = sourceStoredFileId,
        };
        int agreementCountBefore;
        await using (var before = Scope())
        {
            agreementCountBefore = await before.Db.LeaseAgreements.CountAsync(row =>
                row.PortfolioId == _portfolioId);
        }

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityType.Should().Be(nameof(ScanConfirmationTargetKind.LeaseEndingNotice));
        result.Value.TargetEntityId.Should().Be(_leaseManagementId);
        result.Value.LeaseManagementId.Should().Be(_leaseManagementId);
        result.Value.UnitId.Should().Be(_unitId);
        await using var verify = Scope();
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == _leaseManagementId);
        relationship.EndingDisposition.Should().Be(LeaseManagementEndingDisposition.NonRenewalMoveOut);
        relationship.NoticeGivenAtUtc.Should().Be(new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc));
        relationship.PlannedMoveOutAtUtc.Should().Be(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc));
        relationship.EndingDispositionDecidedByUserId.Should().Be(_actorUserId);
        relationship.EndingDispositionDecidedAtUtc.Should().NotBeNull();
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityType.Should().Be(nameof(LeaseManagement));
        source.EntityId.Should().Be(_leaseManagementId);
        (await verify.Db.LeaseAgreements.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(agreementCountBefore);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == result.AttemptId
            && row.EntityType == nameof(LeaseManagement)
            && row.EntityId == _leaseManagementId
            && row.Operation == AuditLogOperation.Updated))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task LeaseEndingNoticeScanConfirmation_ReplayDoesNotDuplicateAgreementOrDispositionEffects()
    {
        SkipIfDockerUnavailable();
        await MarkBaseRelationshipOccupiedAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.LeaseEndingNotice);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "lease-ending-notice-replay");
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var command = Command(draftId, ScanConfirmationTargetKind.LeaseEndingNotice) with
        {
            SourceStoredFileId = sourceStoredFileId,
        };

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.LeaseAgreements.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(1);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(LeaseManagement)
            && row.EntityId == _leaseManagementId
            && row.Operation == AuditLogOperation.Updated))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(1);
        (await verify.Db.StoredFiles.CountAsync(row =>
            row.EntityType == nameof(LeaseManagement)
            && row.EntityId == _leaseManagementId))
            .Should().Be(1);
    }

    [SkippableTheory]
    [InlineData("audit")]
    [InlineData("outbox")]
    public async Task LeaseEndingNoticeScanConfirmation_AuditOrOutboxFailureRollsBackRelationshipDraftSourceAndReceipt(
        string failurePoint)
    {
        SkipIfDockerUnavailable();
        await MarkBaseRelationshipOccupiedAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.LeaseEndingNotice);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, $"lease-ending-notice-{failurePoint}-failure");
        var command = Command(draftId, ScanConfirmationTargetKind.LeaseEndingNotice) with
        {
            SourceStoredFileId = sourceStoredFileId,
        };
        await InstallLeaseEndingNoticeFailureTriggerAsync(failurePoint);

        try
        {
            var action = () => ExecuteAtomicAsync(identity, command, Codec);

            await action.Should().ThrowAsync<Exception>()
                .Where(ex => ex.ToString().Contains(
                    $"fail scan lease ending notice {failurePoint}", StringComparison.Ordinal));
        }
        finally
        {
            await RemoveLeaseEndingNoticeFailureTriggerAsync(failurePoint);
        }

        await using var verify = Scope();
        var draft = await verify.Db.ScanDrafts.AsNoTracking()
            .SingleAsync(row => row.Id == draftId);
        draft.Status.Should().Be("Reviewing");
        var relationship = await verify.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == _leaseManagementId);
        relationship.EndingDisposition.Should().Be(LeaseManagementEndingDisposition.Undecided);
        relationship.EndingDispositionDecidedAtUtc.Should().BeNull();
        relationship.EndingDispositionDecidedByUserId.Should().BeNull();
        relationship.NoticeGivenAtUtc.Should().BeNull();
        relationship.PlannedMoveOutAtUtc.Should().BeNull();
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityType.Should().Be(nameof(ScanConfirmationTargetKind.LeaseEndingNotice));
        source.EntityId.Should().BeNull();
        (await verify.Db.LeaseAgreements.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task ExpenseScanConfirmation_PersistsOperationalScopeMatchingTargetLocation()
    {
        SkipIfDockerUnavailable();

        await AssertExpenseScopeAsync(
            "property-scope",
            _propertyId,
            null,
            null,
            ExpenseOperationalScope.Property,
            expectedPropertyId: _propertyId,
            expectedUnitId: null,
            expectedWorkOrderId: null,
            expectedAllocationTargetKind: ExpenseAllocationTargetKind.Property,
            expectedAllocationPropertyId: _propertyId,
            expectedAllocationUnitId: null);
        await AssertExpenseScopeAsync(
            "unit-scope",
            null,
            _unitId,
            null,
            ExpenseOperationalScope.Unit,
            expectedPropertyId: _propertyId,
            expectedUnitId: _unitId,
            expectedWorkOrderId: null,
            expectedAllocationTargetKind: ExpenseAllocationTargetKind.Unit,
            expectedAllocationPropertyId: null,
            expectedAllocationUnitId: _unitId);
        await AssertExpenseScopeAsync(
            "work-order-scope",
            null,
            null,
            _workOrderId,
            ExpenseOperationalScope.WorkOrder,
            expectedPropertyId: _propertyId,
            expectedUnitId: _unitId,
            expectedWorkOrderId: _workOrderId,
            expectedAllocationTargetKind: ExpenseAllocationTargetKind.Unit,
            expectedAllocationPropertyId: null,
            expectedAllocationUnitId: _unitId);
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

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.Expenses.CountAsync(row => row.Description == uniqueVendor))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task ConfirmReplay_RejectsStaleAuthorization()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Expense);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "stale-replay-authorization");
        var command = Command(draftId, ScanConfirmationTargetKind.Expense);
        await ExecuteAtomicAsync(identity, command, Codec);

        await using (var revoke = Scope())
        {
            await revoke.Db.AuthSessions.Where(session => session.Id == _authSessionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                    .SetProperty(session => session.RevokedAtUtc, CommandTime.AddMinutes(1)));
        }

        try
        {
            var replay = () => ExecuteAtomicAsync(identity, command, Codec);
            await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            await using var restore = Scope();
            await restore.Db.AuthSessions.Where(session => session.Id == _authSessionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.Status, AuthSessionStatus.Active)
                    .SetProperty(session => session.RevokedAtUtc, (DateTime?)null));
        }
    }

    [SkippableFact]
    public async Task PropertyAcquisitionScanConfirmation_EnrichesExistingPropertyOwnershipAndSourceWithoutLoan()
    {
        SkipIfDockerUnavailable();
        var ownerId = await SeedOwnerAsync("Deed Owner");
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.PropertyAcquisition);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "property-acquisition-confirm");
        var command = Command(draftId, ScanConfirmationTargetKind.PropertyAcquisition) with
        {
            SourceStoredFileId = sourceStoredFileId,
            CaptureContext = new ScanCaptureContextData(
                WorkspaceExperience.Management,
                _accessContextId,
                _accessRevision,
                _propertyId,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.PropertyAcquisition,
                PropertyAcquisition: PropertyAcquisitionTarget(
                    _propertyId,
                    ownerId,
                    purchasePrice: 315_000m,
                    landValue: 65_000m)),
        };
        int propertyCountBefore;
        int loanCountBefore;
        await using (var before = Scope())
        {
            propertyCountBefore = await before.Db.Properties.CountAsync(row => row.PortfolioId == _portfolioId);
            loanCountBefore = await before.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId);
        }

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityType.Should().Be(nameof(ScanConfirmationTargetKind.PropertyAcquisition));
        result.Value.TargetEntityId.Should().Be(_propertyId);
        await using var verify = Scope();
        (await verify.Db.Properties.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(propertyCountBefore);
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(loanCountBefore);
        var property = await verify.Db.Properties.AsNoTracking()
            .SingleAsync(row => row.Id == _propertyId);
        property.PurchasePrice.Should().Be(315_000m);
        property.LandValue.Should().Be(65_000m);
        property.InServiceDate.Should().Be(CommandTime.Date);
        property.Notes.Should().Contain("Reviewed deed");
        var ownership = await verify.Db.PropertyOwnerships.AsNoTracking()
            .SingleAsync(row => row.PropertyId == _propertyId && row.EffectiveToUtc == null);
        ownership.OwnerEntityId.Should().Be(ownerId);
        ownership.OwnershipSharePercent.Should().Be(100m);
        ownership.EffectiveFromUtc.Should().Be(CommandTime.Date);
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityType.Should().Be(nameof(Property));
        source.EntityId.Should().Be(_propertyId);
    }

    [SkippableFact]
    public async Task PropertyAcquisitionScanConfirmation_ReplayDoesNotDuplicatePropertyOwnershipOrLoan()
    {
        SkipIfDockerUnavailable();
        var ownerId = await SeedOwnerAsync("Replay Deed Owner");
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.PropertyAcquisition);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "property-acquisition-replay");
        var command = Command(draftId, ScanConfirmationTargetKind.PropertyAcquisition) with
        {
            SourceStoredFileId = sourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.PropertyAcquisition,
                PropertyAcquisition: PropertyAcquisitionTarget(_propertyId, ownerId)),
        };

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.Properties.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(1);
        (await verify.Db.PropertyOwnerships.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.PropertyId == _propertyId
            && row.OwnerEntityId == ownerId))
            .Should().Be(1);
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task PropertyAcquisitionScanConfirmation_AuditFailureRollsBackPropertyOwnershipSourceAndReceipt()
    {
        SkipIfDockerUnavailable();
        var ownerId = await SeedOwnerAsync("Rollback Deed Owner");
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.PropertyAcquisition);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "property-acquisition-audit-failure");
        var command = Command(draftId, ScanConfirmationTargetKind.PropertyAcquisition) with
        {
            SourceStoredFileId = sourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.PropertyAcquisition,
                PropertyAcquisition: PropertyAcquisitionTarget(_propertyId, ownerId)),
        };
        await using (var arrange = Scope())
        {
            await arrange.Db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION fail_scan_property_ownership_audit()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW."EntityType" = 'PropertyOwnership' THEN
                        RAISE EXCEPTION 'fail scan property ownership audit';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                DROP TRIGGER IF EXISTS fail_scan_property_ownership_audit ON "AtomicAuditLogs";
                CREATE TRIGGER fail_scan_property_ownership_audit
                BEFORE INSERT ON "AtomicAuditLogs"
                FOR EACH ROW EXECUTE FUNCTION fail_scan_property_ownership_audit();
                """);
        }

        try
        {
            var action = () => ExecuteAtomicAsync(identity, command, Codec);

            await action.Should().ThrowAsync<Exception>()
                .Where(ex => ex.ToString().Contains(
                    "fail scan property ownership audit", StringComparison.Ordinal));
        }
        finally
        {
            await using var cleanup = Scope();
            await cleanup.Db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_scan_property_ownership_audit ON "AtomicAuditLogs";
                DROP FUNCTION IF EXISTS fail_scan_property_ownership_audit();
                """);
        }

        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var property = await verify.Db.Properties.AsNoTracking().SingleAsync(row => row.Id == _propertyId);
        property.PurchasePrice.Should().BeNull();
        property.LandValue.Should().BeNull();
        property.InServiceDate.Should().BeNull();
        (await verify.Db.PropertyOwnerships.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.PropertyId == _propertyId))
            .Should().Be(0);
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityId.Should().BeNull();
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task PropertyAcquisitionScanConfirmation_WrongCapturedPropertyRejectsAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var ownerId = await SeedOwnerAsync("Wrong Property Owner");
        int otherPropertyId;
        await using (var arrange = Scope())
        {
            var other = new Property
            {
                PortfolioId = _portfolioId,
                Name = "Other Scan House",
                AddressLine1 = "2 Main St",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
                CreatedAt = CommandTime,
                UpdatedAt = CommandTime,
            };
            arrange.Db.Properties.Add(other);
            await arrange.Db.SaveChangesAsync();
            otherPropertyId = other.Id;
        }
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.PropertyAcquisition);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "property-acquisition-wrong-property");
        var command = Command(draftId, ScanConfirmationTargetKind.PropertyAcquisition) with
        {
            SourceStoredFileId = sourceStoredFileId,
            CaptureContext = new ScanCaptureContextData(
                WorkspaceExperience.Management,
                _accessContextId,
                _accessRevision,
                _propertyId,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.PropertyAcquisition,
                PropertyAcquisition: PropertyAcquisitionTarget(otherPropertyId, ownerId)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*property it was scanned from*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.PropertyOwnerships.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.PropertyId == otherPropertyId))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchesExistingScheduledPaymentWithExactStatementValuesAndDoesNotCreateLoan()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment");
        int loanCountBefore;
        await using (var before = Scope())
        {
            loanCountBefore = await before.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId);
        }
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            SourceStoredFileId = sourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, paymentId)),
        };

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityId.Should().Be(loanId);
        result.Value.LoanPaymentId.Should().Be(paymentId);
        await using var verify = Scope();
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(loanCountBefore);
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Scheduled);
        payment.DueDate.Should().Be(new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc));
        payment.PaidDate.Should().BeNull();
        payment.PrincipalAmount.Should().Be(255m);
        payment.InterestAmount.Should().Be(995m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_550m);
        payment.BalanceAfter.Should().Be(199_495m);
        var correction = await verify.Db.LoanPaymentCorrections.AsNoTracking()
            .SingleAsync(row => row.LoanPaymentId == paymentId);
        correction.AttemptId.Should().Be(result.AttemptId);
        correction.SourceScanDraftId.Should().Be(draftId);
        correction.Status.Should().Be(LoanPaymentStatus.Paid);
        correction.DueDate.Should().Be(StatementDate);
        correction.PaidDate.Should().Be(StatementDate);
        correction.PrincipalAmount.Should().Be(StatementPrincipal);
        correction.InterestAmount.Should().Be(StatementInterest);
        correction.EscrowAmount.Should().Be(StatementEscrow);
        correction.TotalAmount.Should().Be(StatementTotal);
        correction.BalanceAfter.Should().Be(StatementBalanceAfter);
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(StatementBalanceAfter);
        loan.Status.Should().Be(LoanStatus.Active);
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityType.Should().Be(nameof(Loan));
        source.EntityId.Should().Be(loanId);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == result.AttemptId
            && row.EntityType == nameof(Loan)
            && row.EntityId == loanId
            && row.Operation == AuditLogOperation.Updated))
            .Should().Be(1);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == result.AttemptId
            && row.EntityType == nameof(LoanPaymentCorrection)
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == $"{command.DeliveryIdempotencyKey}:LoanPayment:{paymentId}:data-update"
            || row.IdempotencyKey == $"{command.DeliveryIdempotencyKey}:Loan:{loanId}:data-update"))
            .Should().Be(2);
        (await verify.Db.JournalEntries.CountAsync(row =>
            row.PortfolioId == _portfolioId
            && row.SourceType == JournalSourceType.LoanPayment
            && row.Lines.Any(line => line.SourceLineId == paymentId)))
            .Should().Be(0);

        var postCommand = AtomicMoneyMutation.Command(
            new WorkspaceReadScope(
                _portfolioId,
                _actorUserId,
                _authSessionId,
                _accessContextId,
                _accessRevision),
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan,
            AtomicMoneyOperation.PostPayment,
            paymentId,
            "scan-corrected-loan-payment",
            new PostLoanPaymentRequest { LoanId = loanId, PaidDate = StatementDate },
            CommandTime.AddMinutes(1));
        var posted = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(postCommand), postCommand, AtomicMoneyMutation.Codec);
        posted.Disposition.Should().Be(AtomicCommandDisposition.Executed);

        var postedLines = await verify.Db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == _portfolioId
                && line.JournalEntry.SourceType == JournalSourceType.LoanPayment
                && line.JournalEntry.SourceId == correction.Id)
            .OrderBy(line => line.Id)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey,
                line.DebitAmount,
                line.CreditAmount,
            })
            .ToListAsync();
        postedLines.Should().Equal(
            new { SystemKey = "mortgage-payable", DebitAmount = StatementPrincipal, CreditAmount = 0m },
            new { SystemKey = "mortgage-interest", DebitAmount = StatementInterest, CreditAmount = 0m },
            new { SystemKey = "mortgage-escrow-asset", DebitAmount = StatementEscrow, CreditAmount = 0m },
            new { SystemKey = "operating-cash", DebitAmount = 0m, CreditAmount = StatementTotal });
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_ArborAndBriarAppendExactEffectiveFebruaryStatementTotals()
    {
        SkipIfDockerUnavailable();
        int arborLoanId;
        int arborPaymentId;
        int briarLoanId;
        int briarPaymentId;
        await using (var arrange = Scope())
        {
            var arborLoan = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = _propertyId,
                Lender = "Arbor statement proof",
                OriginalAmount = 200_000m,
                CurrentBalance = 125_394m,
                AnnualInterestRatePct = 4.5m,
                TermMonths = 360,
                StartDate = CommandTime.AddYears(-10),
                DebtServiceAutomationStartDate = CommandTime.AddMonths(-2),
                DayOfMonthDue = 20,
                MonthlyPrincipalInterest = 1_046m,
                MonthlyEscrow = 318m,
                Status = LoanStatus.Active,
                CreatedAt = CommandTime.AddMonths(-2),
                UpdatedAt = CommandTime.AddMonths(-2),
            };
            var briarLoan = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = _propertyId,
                Lender = "Briar statement proof",
                OriginalAmount = 200_000m,
                CurrentBalance = 130_458m,
                AnnualInterestRatePct = 5.8m,
                TermMonths = 360,
                StartDate = CommandTime.AddYears(-10),
                DebtServiceAutomationStartDate = CommandTime.AddMonths(-2),
                DayOfMonthDue = 20,
                MonthlyPrincipalInterest = 1_070m,
                MonthlyEscrow = 318m,
                Status = LoanStatus.Active,
                CreatedAt = CommandTime.AddMonths(-2),
                UpdatedAt = CommandTime.AddMonths(-2),
            };
            arrange.Db.Loans.AddRange(arborLoan, briarLoan);
            await arrange.Db.SaveChangesAsync();

            var arborPayment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = arborLoan.Id,
                PeriodKey = "2027-02",
                DueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 470.23m,
                PrincipalAmount = 583.77m,
                EscrowAmount = 318m,
                TotalAmount = 1_372m,
                BalanceAfter = 124_810.23m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = CommandTime,
            };
            var briarPayment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = briarLoan.Id,
                PeriodKey = "2027-02",
                DueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 633.81m,
                PrincipalAmount = 444.19m,
                EscrowAmount = 0m,
                TotalAmount = 1_078m,
                BalanceAfter = 130_013.81m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = CommandTime,
            };
            arrange.Db.LoanPayments.AddRange(arborPayment, briarPayment);
            await arrange.Db.SaveChangesAsync();
            arborLoanId = arborLoan.Id;
            arborPaymentId = arborPayment.Id;
            briarLoanId = briarLoan.Id;
            briarPaymentId = briarPayment.Id;
        }

        var arborDraftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var briarDraftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var effectiveDate = new DateTime(2027, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var arborCommand = Command(arborDraftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(
                    _propertyId,
                    arborLoanId,
                    arborPaymentId,
                    openingBalance: 125_394m,
                    principal: 431m,
                    interest: 615m,
                    principalInterest: 1_046m,
                    escrow: 318m,
                    total: 1_364m,
                    effectiveDate: effectiveDate)),
        };
        var briarCommand = Command(briarDraftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(
                    _propertyId,
                    briarLoanId,
                    briarPaymentId,
                    openingBalance: 130_458m,
                    principal: 442m,
                    interest: 628m,
                    principalInterest: 1_070m,
                    escrow: 318m,
                    total: 1_388m,
                    effectiveDate: effectiveDate)),
        };

        await using (var before = Scope())
        {
            var preConfirmation = await LoanPaymentEffectiveQuery.From(before.Db)
                .Where(row => row.Id == arborPaymentId || row.Id == briarPaymentId)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Scheduled = group.Count(row => row.Status == LoanPaymentStatus.Scheduled),
                    PaidCash = group.Sum(row =>
                        row.Status == LoanPaymentStatus.Paid ? row.TotalAmount : 0m),
                })
                .SingleAsync();
            preConfirmation.Scheduled.Should().Be(2);
            preConfirmation.PaidCash.Should().Be(0m);
        }

        var arbor = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, arborDraftId, "ys295-arbor-february-statement"),
            arborCommand,
            Codec);
        var briar = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, briarDraftId, "ys295-briar-february-statement"),
            briarCommand,
            Codec);

        await using var verify = Scope();
        var effectiveRows = await LoanPaymentEffectiveQuery.From(verify.Db)
            .Where(row => row.Id == arborPaymentId || row.Id == briarPaymentId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        effectiveRows.Should().HaveCount(2);
        var effectiveArbor = effectiveRows[0];
        effectiveArbor.Id.Should().Be(arborPaymentId);
        effectiveArbor.DueDate.Should().Be(effectiveDate);
        effectiveArbor.Status.Should().Be(LoanPaymentStatus.Paid);
        effectiveArbor.PrincipalAmount.Should().Be(431m);
        effectiveArbor.InterestAmount.Should().Be(615m);
        effectiveArbor.EscrowAmount.Should().Be(318m);
        effectiveArbor.TotalAmount.Should().Be(1_364m);
        effectiveArbor.BalanceAfter.Should().Be(124_963m);
        var effectiveBriar = effectiveRows[1];
        effectiveBriar.Id.Should().Be(briarPaymentId);
        effectiveBriar.DueDate.Should().Be(effectiveDate);
        effectiveBriar.Status.Should().Be(LoanPaymentStatus.Paid);
        effectiveBriar.PrincipalAmount.Should().Be(442m);
        effectiveBriar.InterestAmount.Should().Be(628m);
        effectiveBriar.EscrowAmount.Should().Be(318m);
        effectiveBriar.TotalAmount.Should().Be(1_388m);
        effectiveBriar.BalanceAfter.Should().Be(130_016m);

        var totals = await LoanPaymentEffectiveQuery.From(verify.Db)
            .Where(row => row.Id == arborPaymentId || row.Id == briarPaymentId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Cash = group.Sum(row => row.TotalAmount),
                Principal = group.Sum(row => row.PrincipalAmount),
                Interest = group.Sum(row => row.InterestAmount),
                Escrow = group.Sum(row => row.EscrowAmount),
                Paid = group.Count(row => row.Status == LoanPaymentStatus.Paid),
            })
            .SingleAsync();
        totals.Cash.Should().Be(2_752m);
        totals.Principal.Should().Be(873m);
        totals.Interest.Should().Be(1_243m);
        totals.Escrow.Should().Be(636m);
        totals.Paid.Should().Be(2);
        (await verify.Db.LoanPaymentCorrections.CountAsync(row =>
            row.LoanPaymentId == arborPaymentId || row.LoanPaymentId == briarPaymentId))
            .Should().Be(2);
        (await verify.Db.Loans.Where(row => row.Id == arborLoanId)
            .Select(row => row.CurrentBalance)
            .SingleAsync()).Should().Be(124_963m);
        (await verify.Db.Loans.Where(row => row.Id == briarLoanId)
            .Select(row => row.CurrentBalance)
            .SingleAsync()).Should().Be(130_016m);
        arbor.Value.LoanPaymentId.Should().Be(arborPaymentId);
        briar.Value.LoanPaymentId.Should().Be(briarPaymentId);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_DuplicateLegacySourceHashForSamePropertyRejectsBeforeCreatingSecondActiveLoan()
    {
        SkipIfDockerUnavailable();
        var sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("same mortgage source content")));
        var firstDraftId = await SeedDraftAsync(
            ScanConfirmationTargetKind.Loan,
            sourceHash,
            legacyStoredFileHashOnly: true);
        var firstSourceStoredFileId = await SourceStoredFileIdAsync(firstDraftId);
        var firstIdentity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, firstDraftId, "loan-duplicate-source-first");
        var firstCommand = Command(firstDraftId, ScanConfirmationTargetKind.Loan) with
        {
            SourceStoredFileId = firstSourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: LoanTarget(_propertyId)),
        };

        var first = await ExecuteAtomicAsync(firstIdentity, firstCommand, Codec);

        first.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        var secondDraftId = await SeedDraftAsync(
            ScanConfirmationTargetKind.Loan,
            sourceHash,
            legacyStoredFileHashOnly: true);
        var secondSourceStoredFileId = await SourceStoredFileIdAsync(secondDraftId);
        var secondIdentity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, secondDraftId, "loan-duplicate-source-second");
        var secondCommand = Command(secondDraftId, ScanConfirmationTargetKind.Loan) with
        {
            SourceStoredFileId = secondSourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: LoanTarget(_propertyId) with { Lender = "Second Copy Bank" }),
        };
        int loanCountAfterFirst;
        await using (var before = Scope())
        {
            loanCountAfterFirst = await before.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId);
        }

        var action = () => ExecuteAtomicAsync(secondIdentity, secondCommand, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*already confirmed as active loan*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == secondDraftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(loanCountAfterFirst);
        var secondSource = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == secondSourceStoredFileId);
        secondSource.EntityId.Should().BeNull();
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == secondIdentity.CommandType
            && row.IdempotencyKey == secondIdentity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchReplayDoesNotDoubleMutateOrCreateLoan()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-replay");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, paymentId)),
        };

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId && row.Id == loanId))
            .Should().Be(1);
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(1);
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Scheduled);
        payment.DueDate.Should().Be(new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc));
        payment.PaidDate.Should().BeNull();
        payment.PrincipalAmount.Should().Be(255m);
        payment.InterestAmount.Should().Be(995m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_550m);
        payment.BalanceAfter.Should().Be(199_495m);
        var correction = await verify.Db.LoanPaymentCorrections.AsNoTracking()
            .SingleAsync(row => row.LoanPaymentId == paymentId);
        correction.DueDate.Should().Be(StatementDate);
        correction.PaidDate.Should().Be(StatementDate);
        correction.PrincipalAmount.Should().Be(StatementPrincipal);
        correction.InterestAmount.Should().Be(StatementInterest);
        correction.EscrowAmount.Should().Be(StatementEscrow);
        correction.TotalAmount.Should().Be(StatementTotal);
        correction.BalanceAfter.Should().Be(StatementBalanceAfter);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(LoanPaymentCorrection)
            && row.EntityId == correction.Id
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchAcceptsAlreadyPaidPaymentWhenStatementValuesMatchWithoutRewrite()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var paidDate = StatementDate.AddDays(-2);
        await using (var arrange = Scope())
        {
            var arrangedPayment = await arrange.Db.LoanPayments
                .Include(row => row.Loan)
                .SingleAsync(row => row.Id == paymentId);
            arrangedPayment.DueDate = paidDate;
            arrangedPayment.PaidDate = paidDate;
            arrangedPayment.Status = LoanPaymentStatus.Paid;
            arrangedPayment.PrincipalAmount = 400m;
            arrangedPayment.InterestAmount = 600m;
            arrangedPayment.EscrowAmount = 300m;
            arrangedPayment.TotalAmount = 1_300m;
            arrangedPayment.BalanceAfter = 125_425m;
            arrangedPayment.Loan!.CurrentBalance = 125_425m;
            await arrange.Db.SaveChangesAsync();
        }

        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-already-paid");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(
                    _propertyId,
                    loanId,
                    paymentId,
                    principal: 400m,
                    interest: 600m,
                    principalInterest: 1_000m,
                    escrow: 300m,
                    total: 1_300m,
                    effectiveDate: StatementDate)),
        };

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityId.Should().Be(loanId);
        result.Value.LoanPaymentId.Should().Be(paymentId);
        await using var verify = Scope();
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Paid);
        payment.DueDate.Should().Be(paidDate);
        payment.PaidDate.Should().Be(paidDate);
        payment.PrincipalAmount.Should().Be(400m);
        payment.InterestAmount.Should().Be(600m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_300m);
        payment.BalanceAfter.Should().Be(125_425m);
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(125_425m);
        var correction = await verify.Db.LoanPaymentCorrections.AsNoTracking()
            .SingleAsync(row => row.LoanPaymentId == paymentId);
        correction.DueDate.Should().Be(StatementDate);
        correction.PaidDate.Should().Be(paidDate);
        correction.PrincipalAmount.Should().Be(400m);
        correction.InterestAmount.Should().Be(600m);
        correction.EscrowAmount.Should().Be(300m);
        correction.TotalAmount.Should().Be(1_300m);
        correction.BalanceAfter.Should().Be(125_425m);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(LoanPaymentCorrection)
            && row.EntityId == correction.Id
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchCorrectsAlreadyPaidRoundedStatementSplitWithoutDuplicateCash()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var paidDate = StatementDate.AddDays(-5);
        await using (var arrange = Scope())
        {
            var arrangedPayment = await arrange.Db.LoanPayments
                .Include(row => row.Loan)
                .SingleAsync(row => row.Id == paymentId);
            arrangedPayment.DueDate = paidDate;
            arrangedPayment.PaidDate = paidDate;
            arrangedPayment.Status = LoanPaymentStatus.Paid;
            arrangedPayment.PrincipalAmount = 464.24m;
            arrangedPayment.InterestAmount = 661.76m;
            arrangedPayment.EscrowAmount = 318m;
            arrangedPayment.TotalAmount = 1_444m;
            arrangedPayment.BalanceAfter = 140_585.76m;
            arrangedPayment.Loan!.CurrentBalance = 140_585.76m;
            await arrange.Db.SaveChangesAsync();
        }

        var initialPostCommand = AtomicMoneyMutation.Command(
            new WorkspaceReadScope(
                _portfolioId,
                _actorUserId,
                _authSessionId,
                _accessContextId,
                _accessRevision),
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan,
            AtomicMoneyOperation.PostPayment,
            paymentId,
            "scan-paid-loan-payment-original",
            new PostLoanPaymentRequest { LoanId = loanId, PaidDate = paidDate },
            CommandTime);
        var initialPosted = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(initialPostCommand),
            initialPostCommand,
            AtomicMoneyMutation.Codec);
        initialPosted.Disposition.Should().Be(AtomicCommandDisposition.Executed);

        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-already-paid-rounded-statement");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(
                    _propertyId,
                    loanId,
                    paymentId,
                    openingBalance: 141_050m,
                    principal: 464m,
                    interest: 662m,
                    principalInterest: 1_126m,
                    escrow: 318m,
                    total: 1_444m,
                    effectiveDate: StatementDate)),
        };

        var result = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        result.Value.TargetEntityId.Should().Be(loanId);
        result.Value.LoanPaymentId.Should().Be(paymentId);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(result.Value);
        await using var verify = Scope();
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Paid);
        payment.DueDate.Should().Be(paidDate);
        payment.PaidDate.Should().Be(paidDate);
        payment.PrincipalAmount.Should().Be(464.24m);
        payment.InterestAmount.Should().Be(661.76m);
        payment.EscrowAmount.Should().Be(318m);
        payment.TotalAmount.Should().Be(1_444m);
        payment.BalanceAfter.Should().Be(140_585.76m);
        var correction = await verify.Db.LoanPaymentCorrections.AsNoTracking()
            .SingleAsync(row => row.LoanPaymentId == paymentId);
        correction.DueDate.Should().Be(StatementDate);
        correction.PaidDate.Should().Be(paidDate);
        correction.PrincipalAmount.Should().Be(464m);
        correction.InterestAmount.Should().Be(662m);
        correction.EscrowAmount.Should().Be(318m);
        correction.TotalAmount.Should().Be(1_444m);
        correction.BalanceAfter.Should().Be(140_586m);
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(140_586m);
        (await verify.Db.Loans.CountAsync(row => row.PortfolioId == _portfolioId))
            .Should().Be(1);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(LoanPaymentCorrection)
            && row.EntityId == correction.Id
            && row.Operation == AuditLogOperation.Created))
            .Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);

        var entries = await verify.Db.JournalEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.LoanPayment
                && entry.Lines.Any(line => line.SourceLineId == paymentId))
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.LedgerAccount)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
        entries.Should().HaveCount(3);
        var original = entries.Single(entry => entry.SourceId == paymentId
            && entry.ReversesJournalEntryId is null);
        var reversal = entries.Single(entry => entry.ReversesJournalEntryId == original.Id);
        var replacement = entries.Single(entry => entry.SourceId == correction.Id
            && entry.ReversesJournalEntryId is null);
        reversal.Lines.Should().Contain(line =>
            line.LedgerAccount!.SystemKey == "operating-cash"
            && line.DebitAmount == 1_444m
            && line.CreditAmount == 0m);
        replacement.Lines.Should().Contain(line =>
            line.LedgerAccount!.SystemKey == "mortgage-payable"
            && line.DebitAmount == 464m
            && line.CreditAmount == 0m);
        replacement.Lines.Should().Contain(line =>
            line.LedgerAccount!.SystemKey == "mortgage-interest"
            && line.DebitAmount == 662m
            && line.CreditAmount == 0m);
        replacement.Lines.Should().Contain(line =>
            line.LedgerAccount!.SystemKey == "mortgage-escrow-asset"
            && line.DebitAmount == 318m
            && line.CreditAmount == 0m);
        replacement.Lines.Should().Contain(line =>
            line.LedgerAccount!.SystemKey == "operating-cash"
            && line.DebitAmount == 0m
            && line.CreditAmount == 1_444m);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchRejectsAlreadyPaidPaymentWhenStatementValuesDisagreeAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var paidDate = StatementDate.AddDays(-5);
        await using (var arrange = Scope())
        {
            var arrangedPayment = await arrange.Db.LoanPayments
                .Include(row => row.Loan)
                .SingleAsync(row => row.Id == paymentId);
            arrangedPayment.DueDate = paidDate;
            arrangedPayment.PaidDate = paidDate;
            arrangedPayment.Status = LoanPaymentStatus.Paid;
            arrangedPayment.PrincipalAmount = 400m;
            arrangedPayment.InterestAmount = 600m;
            arrangedPayment.EscrowAmount = 300m;
            arrangedPayment.TotalAmount = 1_300m;
            arrangedPayment.BalanceAfter = 125_425m;
            arrangedPayment.Loan!.CurrentBalance = 125_425m;
            await arrange.Db.SaveChangesAsync();
        }

        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-already-paid-mismatch");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(
                    _propertyId,
                    loanId,
                    paymentId,
                    principal: 401m,
                    interest: 599m,
                    principalInterest: 1_000m,
                    escrow: 300m,
                    total: 1_300m,
                    effectiveDate: StatementDate)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*already-paid loan payment*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Paid);
        payment.DueDate.Should().Be(paidDate);
        payment.PaidDate.Should().Be(paidDate);
        payment.PrincipalAmount.Should().Be(400m);
        payment.InterestAmount.Should().Be(600m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_300m);
        payment.BalanceAfter.Should().Be(125_425m);
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(125_425m);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchRejectsWhenEarlierPaymentIsUnpaidAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var (loanId, _, laterPaymentId) = await SeedExistingLoanWithPaymentsAsync(includeEarlierUnpaid: true);
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-earlier-unpaid");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, laterPaymentId)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*Earlier scheduled loan payments must be posted first*");
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == laterPaymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Scheduled);
        payment.PaidDate.Should().BeNull();
        payment.PrincipalAmount.Should().Be(255m);
        payment.InterestAmount.Should().Be(995m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_550m);
        payment.BalanceAfter.Should().Be(199_495m);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchRejectsMismatchedStatementComponentsAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-invalid-components");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, paymentId, total: StatementTotal + 1m)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*Statement total must equal principal plus interest plus escrow*");
        await AssertLoanPaymentMatchRolledBackAsync(draftId, identity, loanId, paymentId);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_MatchRejectsOpeningBalanceMismatchAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-stale-balance");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, paymentId, openingBalance: StatementOpeningBalance - 1m)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*opening unpaid principal must match the loan's current live balance*");
        await AssertLoanPaymentMatchRolledBackAsync(draftId, identity, loanId, paymentId);
    }

    [SkippableFact]
    public async Task LoanScanConfirmation_AuditFailureRollsBackDraftPaymentLoanAuditAndReceipt()
    {
        SkipIfDockerUnavailable();
        var (loanId, paymentId) = await SeedExistingLoanWithPaymentsAsync();
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.Loan);
        var sourceStoredFileId = await SourceStoredFileIdAsync(draftId);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "match-existing-loan-payment-audit-failure");
        var command = Command(draftId, ScanConfirmationTargetKind.Loan) with
        {
            SourceStoredFileId = sourceStoredFileId,
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Loan,
                Loan: MatchExistingLoanTarget(_propertyId, loanId, paymentId)),
        };
        await using (var arrange = Scope())
        {
            await arrange.Db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION fail_scan_loan_payment_audit()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW."EntityType" = 'LoanPaymentCorrection' THEN
                        RAISE EXCEPTION 'fail scan loan payment audit';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                DROP TRIGGER IF EXISTS fail_scan_loan_payment_audit ON "AtomicAuditLogs";
                CREATE TRIGGER fail_scan_loan_payment_audit
                BEFORE INSERT ON "AtomicAuditLogs"
                FOR EACH ROW EXECUTE FUNCTION fail_scan_loan_payment_audit();
                """);
        }

        try
        {
            var action = () => ExecuteAtomicAsync(identity, command, Codec);

            await action.Should().ThrowAsync<Exception>()
                .Where(ex => ex.ToString().Contains(
                    "fail scan loan payment audit", StringComparison.Ordinal));
        }
        finally
        {
            await using var cleanup = Scope();
            await cleanup.Db.Database.ExecuteSqlRawAsync("""
                    DROP TRIGGER IF EXISTS fail_scan_loan_payment_audit ON "AtomicAuditLogs";
                    DROP FUNCTION IF EXISTS fail_scan_loan_payment_audit();
                    """);
        }
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Scheduled);
        payment.PaidDate.Should().BeNull();
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(StatementOpeningBalance);
        var source = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == sourceStoredFileId);
        source.EntityId.Should().BeNull();
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(LoanPaymentCorrection)))
            .Should().Be(0);
        (await verify.Db.LoanPaymentCorrections.CountAsync(row => row.LoanPaymentId == paymentId))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
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

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

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

    [SkippableFact(Skip = "RS-B12 stale test: canonical import now rejects a second initial agreement; receipt #rs-b12-one-initial-agreement")]
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
        var outcome = await ExecuteAtomicAsync(
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

    [SkippableFact(Skip = "RS-B12 stale test: canonical import now rejects a second initial agreement; receipt #rs-b12-one-initial-agreement")]
    public async Task NeedsSignatures_PreservesUploadedScanOnConfirmedDraftAgreement()
    {
        SkipIfDockerUnavailable();
        var source = await SeedLeaseDraftAsync("Lease awaiting signatures");
        var target = LeaseTarget(
            _propertyId,
            _unitId,
            _tenantId,
            leaseManagementId: _leaseManagementId,
            tenantAccountId: _tenantAccountId) with
        {
            ReviewDisposition = LeaseScanReviewDisposition.NeedsSignatures,
        };

        var outcome = await ExecuteAtomicAsync(
            ScanConfirmationCommandIdentity.Create(
                _portfolioId, source.DraftId, "lease-needs-signatures"),
            LeaseCommand(source, target),
            Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var agreement = await verify.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.TargetEntityId);
        agreement.IssuedArtifactId.Should().BeNull();
        agreement.IssuedAtUtc.Should().BeNull();
        agreement.ExecutedArtifactId.Should().BeNull();

        var sourceVersion = await verify.Db.LegalDocumentSourceVersions.AsNoTracking()
            .SingleAsync(row => row.Id == agreement.DocumentSourceVersionId);
        sourceVersion.SourceKind.Should().Be(LegalDocumentSourceKind.BuiltInRenderer);
        sourceVersion.SourceStoredFileId.Should().BeNull();

        var scanDraft = await verify.Db.ScanDrafts.AsNoTracking()
            .SingleAsync(row => row.Id == source.DraftId);
        scanDraft.Status.Should().Be("Confirmed");
        scanDraft.TargetEntityType.Should().Be(nameof(LeaseAgreement));
        scanDraft.ConfirmedEntityId.Should().Be(agreement.Id);
        scanDraft.SourceStoredFileId.Should().Be(source.StoredFileId);

        var storedFile = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == source.StoredFileId);
        storedFile.EntityType.Should().Be(nameof(LeaseAgreement));
        storedFile.EntityId.Should().Be(agreement.Id);
        storedFile.DeletedAt.Should().BeNull();
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

        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

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

        var outcome = await ExecuteAtomicAsync(
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

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

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

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

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

    [SkippableFact]
    public async Task PropertyAcquisitionScanConfirmation_CrossPortfolioPropertyIsUnauthorizedAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var ownerId = await SeedOwnerAsync("Cross Portfolio Owner");
        int foreignPropertyId;
        await using (var arrange = Scope())
        {
            var foreign = new Portfolio
            {
                Name = "Foreign acquisition",
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
                Name = "Foreign Acquisition House",
                AddressLine1 = "991 Other St",
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
        var draftId = await SeedDraftAsync(ScanConfirmationTargetKind.PropertyAcquisition);
        var identity = ScanConfirmationCommandIdentity.Create(
            _portfolioId, draftId, "property-acquisition-foreign-property");
        var command = Command(draftId, ScanConfirmationTargetKind.PropertyAcquisition) with
        {
            Target = new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.PropertyAcquisition,
                PropertyAcquisition: PropertyAcquisitionTarget(foreignPropertyId, ownerId)),
        };

        var action = () => ExecuteAtomicAsync(identity, command, Codec);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        (await verify.Db.PropertyOwnerships.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.PropertyId == foreignPropertyId))
            .Should().Be(0);
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
                WorkOrder: WorkOrderTargetWithAccessPacket(_propertyId)),
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
            ScanConfirmationTargetKind.PropertyAcquisition => new ScanConfirmationTargetData(
                kind,
                PropertyAcquisition: PropertyAcquisitionTarget(
                    _propertyId,
                    _ownerEntityId)),
            ScanConfirmationTargetKind.LeaseEndingNotice => new ScanConfirmationTargetData(
                kind,
                LeaseEndingNotice: LeaseEndingNoticeTarget(_leaseManagementId, _unitId)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        },
        AuthSessionId: _authSessionId,
        AccessContextId: _accessContextId,
        ExpectedAccessRevision: _accessRevision,
        DeliveryIdempotencyKey: $"scan-confirm:{_portfolioId}:{draftId}:{kind}");

    private ScanWorkOrderTargetData WorkOrderTargetWithAccessPacket(
        int propertyId,
        int? unitId = null,
        int? leaseManagementId = null) => new(
        propertyId,
        unitId ?? _unitId,
        _tenantId,
        leaseManagementId ?? _leaseManagementId,
        _vendorId,
        "Leaking sink",
        "Water under sink",
        "Call Morgan before entry, use side gate, and keep the dog crated.",
        "Morgan Resident",
        "555-0134",
        "morgan@example.test",
        true,
        true,
        true,
        true,
        "Preferred window Tuesday 10 AM to noon; key under lockbox.",
        "Dog in crate in bedroom.",
        "Use side gate; front steps are loose.",
        "Plumbing",
        WorkOrderPriority.High,
        150m);

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
        int? expectedWorkOrderId,
        ExpenseAllocationTargetKind? expectedAllocationTargetKind,
        int? expectedAllocationPropertyId,
        int? expectedAllocationUnitId)
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

        var result = await ExecuteAtomicAsync(
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
        var allocations = await verify.Db.ExpenseAllocations.AsNoTracking()
            .Where(row => row.ExpenseId == expense.Id)
            .ToListAsync();
        if (expectedAllocationTargetKind is null)
        {
            allocations.Should().BeEmpty();
        }
        else
        {
            var allocation = allocations.Should().ContainSingle().Subject;
            allocation.TargetKind.Should().Be(expectedAllocationTargetKind);
            allocation.PropertyId.Should().Be(expectedAllocationPropertyId);
            allocation.UnitId.Should().Be(expectedAllocationUnitId);
            allocation.OwnerEntityId.Should().BeNull();
            allocation.Amount.Should().Be(expense.Amount);
        }
    }

    private static ScanLeaseTargetData GuidedSetupManualTarget(string marker) => new(
        PropertyId: 0,
        UnitId: null,
        TenantId: null,
        TenantName: $"Manual {marker} Tenant",
        TenantEmail: $"{marker.ToLowerInvariant()}@example.test",
        TenantPhone: "555-0199",
        TenantEmergencyContact: null,
        PropertyName: $"{marker} House",
        PropertyType: "SingleFamily",
        RentalStructure: RentalStructure.SingleRental,
        PropertyAddress: $"{marker} Street",
        PropertyCity: "Akron",
        PropertyState: "OH",
        PropertyPostalCode: "44308",
        UnitNumber: $"{marker}-unit",
        UnitBedrooms: 2m,
        UnitBathrooms: 1m,
        UnitSquareFeet: 900,
        LeaseNumber: $"{marker}-lease",
        StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate: new DateTime(2027, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent: 1_450m,
        SecurityDeposit: 1_450m,
        LateFee: 75m,
        RentDueDay: 1,
        ReviewDisposition: LeaseScanReviewDisposition.NeedsSignatures,
        TermsPayload: "{}",
        RentTrackingStartMode: RentTrackingStartMode.ForwardOnly);

    private async Task<ManualLeasePersistenceCounts> ReadManualLeasePersistenceCountsAsync(
        string marker,
        string? leaseNumber,
        AtomicCommandIdentity identity)
    {
        await using var verify = Scope();
        return new ManualLeasePersistenceCounts(
            await verify.Db.ScanDrafts.CountAsync(row => row.PortfolioId == _portfolioId),
            await verify.Db.Properties.CountAsync(row => row.PortfolioId == _portfolioId
                && row.AddressLine1 == $"{marker} Street"),
            await verify.Db.Units.CountAsync(row => row.PortfolioId == _portfolioId
                && row.UnitNumber == $"{marker}-unit"),
            await verify.Db.Tenants.CountAsync(row => row.PortfolioId == _portfolioId
                && row.Email == $"{marker.ToLowerInvariant()}@example.test"),
            await verify.Db.LeaseManagements.CountAsync(row => row.PortfolioId == _portfolioId),
            await verify.Db.TenantAccounts.CountAsync(row => row.PortfolioId == _portfolioId),
            await verify.Db.LeaseManagementParties.CountAsync(row => row.PortfolioId == _portfolioId),
            leaseNumber is null
                ? 0
                : await verify.Db.LeaseAgreements.CountAsync(row => row.PortfolioId == _portfolioId
                    && row.AgreementNumber == leaseNumber),
            await verify.Db.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey),
            await verify.Db.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey),
            await verify.Db.OutboxMessages.CountAsync(row => row.IdempotencyKey.Contains(marker)));
    }

    private sealed record ManualLeasePersistenceCounts(
        int Drafts,
        int Properties,
        int Units,
        int Tenants,
        int LeaseManagements,
        int TenantAccounts,
        int LeaseManagementParties,
        int Agreements,
        int Receipts,
        int AuditRows,
        int OutboxRows);

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

    private static ScanPropertyAcquisitionTargetData PropertyAcquisitionTarget(
        int propertyId,
        int ownerEntityId,
        decimal purchasePrice = 250_000m,
        decimal landValue = 50_000m) => new(
        propertyId,
        CommandTime.Date,
        purchasePrice,
        landValue,
        CommandTime.Date,
        "Reviewed deed",
        [
            new ScanPropertyAcquisitionOwnerData(
                ownerEntityId,
                100m,
                "Deed Owner",
                "deed-owner@example.test",
                "Deed Owner")
        ]);

    private static ScanLeaseEndingNoticeTargetData LeaseEndingNoticeTarget(
        int leaseManagementId,
        int unitId) => new(
        leaseManagementId,
        unitId,
        new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc),
        "tenant non-renewal notice",
        "Tenant will not renew.");

    private static ScanLoanTargetData MatchExistingLoanTarget(int propertyId, int loanId, int paymentId) =>
        LoanTarget(propertyId) with
        {
            ExistingLoanId = loanId,
            ExistingLoanPaymentId = paymentId,
            CurrentBalance = StatementOpeningBalance,
            MonthlyPrincipalInterest = StatementPrincipalInterest,
            StatementPrincipalAmount = StatementPrincipal,
            StatementInterestAmount = StatementInterest,
            StatementEscrowAmount = StatementEscrow,
            StatementTotalAmount = StatementTotal,
            StatementEffectiveDate = StatementDate,
        };

    private static ScanLoanTargetData MatchExistingLoanTarget(
        int propertyId,
        int loanId,
        int paymentId,
        decimal openingBalance = StatementOpeningBalance,
        decimal principal = StatementPrincipal,
        decimal interest = StatementInterest,
        decimal principalInterest = StatementPrincipalInterest,
        decimal escrow = StatementEscrow,
        decimal total = StatementTotal,
        DateTime? effectiveDate = null) =>
        LoanTarget(propertyId) with
        {
            ExistingLoanId = loanId,
            ExistingLoanPaymentId = paymentId,
            CurrentBalance = openingBalance,
            MonthlyPrincipalInterest = principalInterest,
            StatementPrincipalAmount = principal,
            StatementInterestAmount = interest,
            StatementEscrowAmount = escrow,
            StatementTotalAmount = total,
            StatementEffectiveDate = effectiveDate ?? StatementDate,
        };

    private static ScanReceiptData Receipt(string vendorName) => new(
        vendorName, null, null, null, null, "R-1", CommandTime,
        120m, 5m, null, null, null, null, 125m, "Check", null,
        ScheduleECategory.Repairs, "Receipt", "Reviewed", null,
        [new ScanReceiptLineData("Part", 1m, 120m, 120m)],
        "Rent payer", "1001", "Test Bank", []);

    private async Task<int> SeedDraftAsync(
        ScanConfirmationTargetKind kind,
        string? sourceContentSha256 = null,
        bool legacyStoredFileHashOnly = false,
        bool draftHashOnly = false)
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
            ContentSha256 = draftHashOnly ? null : sourceContentSha256,
            EntityType = kind.ToString(),
            UploadedAt = CommandTime.AddMinutes(-5),
        };
        var draft = new ScanDraft
        {
            PortfolioId = _portfolioId,
            FilePath = path,
            SourceStoredFile = source,
            SourceContentSha256 = legacyStoredFileHashOnly ? null : sourceContentSha256,
            TargetEntityType = kind.ToString(),
            Status = "Reviewing",
            ExtractedFields = "{\"source\":\"integration-test\"}",
            CreatedAt = CommandTime.AddMinutes(-5),
        };
        scope.Db.AddRange(source, draft);
        await scope.Db.SaveChangesAsync();
        _preparedFingerprints[draft.Id] = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256);
        return draft.Id;
    }

    private async Task<int> SourceStoredFileIdAsync(int draftId)
    {
        await using var scope = Scope();
        return await scope.Db.ScanDrafts.AsNoTracking()
            .Where(row => row.Id == draftId)
            .Select(row => row.SourceStoredFileId!.Value)
            .SingleAsync();
    }

    private async Task<long> SeedOpenTenantChargeAsync(
        decimal amount,
        TenantLedgerEntryType entryType = TenantLedgerEntryType.RentCharge,
        int dueOnOffsetDays = 0)
    {
        await using var scope = Scope();
        var charge = new TenantLedgerEntry
        {
            PortfolioId = _portfolioId,
            TenantAccountId = _tenantAccountId,
            EntryType = entryType,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(CommandTime),
            DueOn = DateOnly.FromDateTime(CommandTime.AddDays(dueOnOffsetDays)),
            PostedAtUtc = CommandTime,
            Description = $"Open {entryType} for scan payment",
            BusinessKey = $"scan-open-charge:{Guid.NewGuid():N}",
            LeaseAgreementId = _leaseAgreementId,
            CreatedByUserId = _actorUserId,
        };
        scope.Db.TenantLedgerEntries.Add(charge);
        await scope.Db.SaveChangesAsync();
        return charge.Id;
    }

    private async Task MarkBaseRelationshipOccupiedAsync()
    {
        await using var scope = Scope();
        var relationship = await scope.Db.LeaseManagements.SingleAsync(
            row => row.Id == _leaseManagementId && row.PortfolioId == _portfolioId);
        relationship.PossessionGivenAtUtc = CommandTime.AddDays(-30);
        relationship.PossessionReturnedAtUtc = null;
        relationship.AccountClosedAtUtc = null;
        relationship.CanceledAtUtc = null;
        relationship.EndingDisposition = LeaseManagementEndingDisposition.Undecided;
        relationship.EndingDispositionDecidedAtUtc = null;
        relationship.EndingDispositionDecidedByUserId = null;
        relationship.NoticeGivenAtUtc = null;
        relationship.PlannedMoveOutAtUtc = null;
        await scope.Db.SaveChangesAsync();
    }

    private async Task InstallLeaseEndingNoticeFailureTriggerAsync(string failurePoint)
    {
        await using var scope = Scope();
        switch (failurePoint)
        {
            case "audit":
                await scope.Db.Database.ExecuteSqlRawAsync("""
                    CREATE OR REPLACE FUNCTION fail_scan_lease_ending_notice_audit()
                    RETURNS trigger AS $$
                    BEGIN
                        IF NEW."EntityType" = 'LeaseManagement' THEN
                            RAISE EXCEPTION 'fail scan lease ending notice audit';
                        END IF;
                        RETURN NEW;
                    END;
                    $$ LANGUAGE plpgsql;
                    DROP TRIGGER IF EXISTS fail_scan_lease_ending_notice_audit ON "AtomicAuditLogs";
                    CREATE TRIGGER fail_scan_lease_ending_notice_audit
                    BEFORE INSERT ON "AtomicAuditLogs"
                    FOR EACH ROW EXECUTE FUNCTION fail_scan_lease_ending_notice_audit();
                    """);
                break;
            case "outbox":
                await scope.Db.Database.ExecuteSqlRawAsync("""
                    CREATE OR REPLACE FUNCTION fail_scan_lease_ending_notice_outbox()
                    RETURNS trigger AS $$
                    BEGIN
                        IF NEW."MessageType" = 'data-update' THEN
                            RAISE EXCEPTION 'fail scan lease ending notice outbox';
                        END IF;
                        RETURN NEW;
                    END;
                    $$ LANGUAGE plpgsql;
                    DROP TRIGGER IF EXISTS fail_scan_lease_ending_notice_outbox ON "OutboxMessages";
                    CREATE TRIGGER fail_scan_lease_ending_notice_outbox
                    BEFORE INSERT ON "OutboxMessages"
                    FOR EACH ROW EXECUTE FUNCTION fail_scan_lease_ending_notice_outbox();
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failurePoint));
        }
    }

    private async Task RemoveLeaseEndingNoticeFailureTriggerAsync(string failurePoint)
    {
        await using var scope = Scope();
        switch (failurePoint)
        {
            case "audit":
                await scope.Db.Database.ExecuteSqlRawAsync("""
                    DROP TRIGGER IF EXISTS fail_scan_lease_ending_notice_audit ON "AtomicAuditLogs";
                    DROP FUNCTION IF EXISTS fail_scan_lease_ending_notice_audit();
                    """);
                break;
            case "outbox":
                await scope.Db.Database.ExecuteSqlRawAsync("""
                    DROP TRIGGER IF EXISTS fail_scan_lease_ending_notice_outbox ON "OutboxMessages";
                    DROP FUNCTION IF EXISTS fail_scan_lease_ending_notice_outbox();
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failurePoint));
        }
    }

    private async Task InstallManualLeaseCascadeFailureTriggerAsync()
    {
        await using var scope = Scope();
        await scope.Db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_manual_lease_cascade()
            RETURNS trigger AS $$
            BEGIN
                IF NEW."RelationshipNumber" LIKE 'LM-SCAN-%' THEN
                    RAISE EXCEPTION 'fail manual lease canonical cascade after draft flush';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS fail_manual_lease_cascade ON "LeaseManagements";
            CREATE TRIGGER fail_manual_lease_cascade
            BEFORE INSERT ON "LeaseManagements"
            FOR EACH ROW EXECUTE FUNCTION fail_manual_lease_cascade();
            """);
    }

    private async Task RemoveManualLeaseCascadeFailureTriggerAsync()
    {
        await using var scope = Scope();
        await scope.Db.Database.ExecuteSqlRawAsync("""
            DROP TRIGGER IF EXISTS fail_manual_lease_cascade ON "LeaseManagements";
            DROP FUNCTION IF EXISTS fail_manual_lease_cascade();
            """);
    }

    private async Task<(int LoanId, int PaymentId)> SeedExistingLoanWithPaymentsAsync()
    {
        var (loanId, _, secondPaymentId) = await SeedExistingLoanWithPaymentsAsync(includeEarlierUnpaid: false);
        return (loanId, secondPaymentId);
    }

    private async Task<(int LoanId, int FirstPaymentId, int SecondPaymentId)> SeedExistingLoanWithPaymentsAsync(
        bool includeEarlierUnpaid)
    {
        await using var scope = Scope();
        var loan = new Loan
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            Lender = "Existing Match Bank",
            OriginalAmount = 200_000m,
            CurrentBalance = StatementOpeningBalance,
            AnnualInterestRatePct = 6.125m,
            TermMonths = 360,
            StartDate = CommandTime.AddYears(-1),
            DebtServiceAutomationStartDate = CommandTime.AddMonths(-2),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 1_250m,
            MonthlyEscrow = 300m,
            EscrowCoversTaxes = true,
            EscrowCoversInsurance = true,
            Status = LoanStatus.Active,
            CreatedAt = CommandTime.AddMonths(-2),
            UpdatedAt = CommandTime.AddMonths(-2),
        };
        scope.Db.Loans.Add(loan);
        await scope.Db.SaveChangesAsync();

        var first = new LoanPayment
        {
            PortfolioId = _portfolioId,
            LoanId = loan.Id,
            PeriodKey = "2026-06",
            DueDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = includeEarlierUnpaid ? null : CommandTime.AddMonths(-1),
            InterestAmount = 1_000m,
            PrincipalAmount = 250m,
            EscrowAmount = 300m,
            TotalAmount = 1_550m,
            BalanceAfter = 199_750m,
            Status = includeEarlierUnpaid ? LoanPaymentStatus.Scheduled : LoanPaymentStatus.Paid,
            CreatedAt = CommandTime.AddMonths(-2),
        };
        var second = new LoanPayment
        {
            PortfolioId = _portfolioId,
            LoanId = loan.Id,
            PeriodKey = "2026-07",
            DueDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            InterestAmount = 995m,
            PrincipalAmount = 255m,
            EscrowAmount = 300m,
            TotalAmount = 1_550m,
            BalanceAfter = 199_495m,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = CommandTime.AddMonths(-2),
        };
        scope.Db.LoanPayments.AddRange(first, second);
        await scope.Db.SaveChangesAsync();
        return (loan.Id, first.Id, second.Id);
    }

    private async Task AssertLoanPaymentMatchRolledBackAsync(
        int draftId,
        AtomicCommandIdentity identity,
        int loanId,
        int paymentId)
    {
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId))
            .Status.Should().Be("Reviewing");
        var payment = await verify.Db.LoanPayments.AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        payment.Status.Should().Be(LoanPaymentStatus.Scheduled);
        payment.PaidDate.Should().BeNull();
        payment.DueDate.Should().Be(new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc));
        payment.PrincipalAmount.Should().Be(255m);
        payment.InterestAmount.Should().Be(995m);
        payment.EscrowAmount.Should().Be(300m);
        payment.TotalAmount.Should().Be(1_550m);
        payment.BalanceAfter.Should().Be(199_495m);
        var loan = await verify.Db.Loans.AsNoTracking().SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(StatementOpeningBalance);
        (await verify.Db.LoanPaymentCorrections.CountAsync(row => row.LoanPaymentId == paymentId))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
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
            ScanConfirmationTargetKind.PropertyAcquisition => db.Properties.AnyAsync(row => row.Id == id),
            ScanConfirmationTargetKind.LeaseEndingNotice => db.LeaseManagements.AnyAsync(row => row.Id == id),
            _ => Task.FromResult(false),
        };

    private async Task<int> SeedOwnerAsync(string name)
    {
        await using var scope = Scope();
        var owner = new OwnerEntity
        {
            PortfolioId = _portfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = name,
            Email = $"{name.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}@example.test",
            CreatedAt = CommandTime,
            UpdatedAt = CommandTime,
        };
        scope.Db.OwnerEntities.Add(owner);
        await scope.Db.SaveChangesAsync();
        return owner.Id;
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope =
            (_services ?? throw new InvalidOperationException()).CreateAsyncScope();
        if (command is ConfirmScanDraftCommand confirm)
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var writer = scope.ServiceProvider.GetRequiredService<IScanConfirmationTargetWriter>();
            var outcome = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey,
                    ScanDraftWriteSupport.Write(
                        identity.CommandType, confirm, ScanDraftWriteSupport.ConfirmResultContract,
                        (request, context, token) => ConfirmScanDraftRule.ExecuteAsync(
                            db, writer, request, context, token),
                        (request, context, token) => ConfirmScanDraftRule.AuthorizeAsync(
                            writer, request, context, token)), ct);
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is CreateManualLeaseCommand manualLease)
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var writer = scope.ServiceProvider.GetRequiredService<IScanConfirmationTargetWriter>();
            var outcome = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey,
                    ScanDraftWriteSupport.Write(
                        identity.CommandType, manualLease, ScanDraftWriteSupport.ConfirmResultContract,
                        (request, context, token) => CreateManualLeaseRule.ExecuteAsync(
                            db, writer, request, context, token),
                        (request, context, token) => CreateManualLeaseRule.AuthorizeAsync(
                            db, request, context, token)), ct);
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is RefundTenantPaymentCommand refund)
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var handler = new RefundTenantPaymentRule(db);
            var outcome = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey,
                    TenantMoneyWriteSupport.Write(
                        refund, handler.ExecuteAsync, handler.AuthorizeAsync), ct);
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is AtomicMoneyMutationCommand money)
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var outcome = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey, AtomicMoneyMutation.Write(money, db), ct);
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        throw new InvalidOperationException($"No executor rule exists for {typeof(TCommand).Name}.");
    }

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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}

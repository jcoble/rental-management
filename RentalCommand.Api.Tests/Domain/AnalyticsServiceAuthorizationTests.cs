using System.Data.Common;
using FluentAssertions;
using RentalCommand.Api.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public sealed class AnalyticsServiceAuthorizationTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;

    public AnalyticsServiceAuthorizationTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task GetOverviewAsync_PreservesDivergentRentValuesInOnePropertyScopedSqlStatement()
    {
        var selected = SeedProperty(
            "Selected Property", "101", WorkOrderPriority.High, 125m, 1_200m, 1_050m, 400m);
        SeedProperty(
            "Forbidden Property", "202", WorkOrderPriority.Emergency, 875m, 2_400m, 2_100m, 600m);
        var scope = SeedSelectedPropertyScope(selected.Id, RoleProfileKeys.WorkspaceAdministrator);
        await _ctx.Db.SaveChangesAsync();
        await FreezeDatabaseBusinessClockAsync(Now);

        var sut = new AnalyticsService(
            _ctx.Db,
            new FixedTimeProvider(Now),
            new FixedAuthSecurityClock(Now.UtcDateTime));
        _commands.Clear();

        var result = await sut.GetOverviewAsync(scope);

        result.TotalUnits.Should().Be(1);
        result.OccupiedUnits.Should().Be(1);
        result.MonthRentScheduled.Should().Be(1_200m);
        result.MonthRentCollected.Should().Be(400m);
        result.Overdue.Should().BeEquivalentTo(new { Count = 1, Amount = 800m });
        result.LeasesExpiring30.Should().Be(1);
        result.MonthlyRecurringRent.Should().Be(1_050m);
        result.OpenWorkOrders.Should().ContainSingle();
        result.OpenWorkOrders.Single().Priority.Should().Be(WorkOrderPriority.High.ToString());
        result.OpenWorkOrders.Single().Count.Should().Be(1);
        result.Trend.Should().HaveCount(12);
        result.Trend.Single(point => point.Month == "2026-07").Expenses.Should().Be(125m);

        _commands.Should().ContainSingle();
        _commands[0].Should().Contain("reports.read");
        _commands[0].Should().Contain(AnalyticsService.AuthorizationSource);
        _commands[0].Should().NotContain("MembershipRoleAssignmentProperties");
        _commands[0].Should().Contain("authorized_properties");
        _commands[0].Should().Contain("vw_unit_occupancy");
        _commands[0].Should().Contain("TenantLedgerEntries");
        _commands[0].Should().Contain("vw_lease_agreement_status");
        _commands[0].Should().Contain("JOIN");
        _commands[0].Should().Contain("sum(");
        _commands[0].Should().Contain("WorkOrders");
        _commands[0].Should().Contain("Expenses");
    }

    [Fact]
    public async Task GetOverviewAsync_ExpenseTrendUsesPaidStatusAndPaidDate()
    {
        var property = SeedProperty(
            "Cash Basis Trend", "505", WorkOrderPriority.Normal, 999m, 1_000m, 1_000m, 0m);
        var seededExpense = _ctx.Db.Expenses.Single(expense => expense.PropertyId == property.Id);
        seededExpense.Status = ExpenseStatus.Pending;

        var createdAt = Now.UtcDateTime;
        _ctx.Db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "January paid repair",
                Status = ExpenseStatus.Paid,
                Amount = 100m,
                IncurredAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                PaidAt = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Insurance,
                Description = "December bill paid in January",
                Status = ExpenseStatus.Paid,
                Amount = 50m,
                IncurredAt = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                PaidAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Pending repair",
                Status = ExpenseStatus.Pending,
                Amount = 200m,
                IncurredAt = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Draft repair",
                Status = ExpenseStatus.Draft,
                Amount = 300m,
                IncurredAt = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Rejected repair",
                Status = ExpenseStatus.Rejected,
                Amount = 400m,
                IncurredAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        await _ctx.Db.SaveChangesAsync();

        var scope = SeedSelectedPropertyScope(property.Id, RoleProfileKeys.WorkspaceAdministrator);
        await _ctx.Db.SaveChangesAsync();
        await FreezeDatabaseBusinessClockAsync(Now);

        var sut = new AnalyticsService(
            _ctx.Db,
            new FixedTimeProvider(Now),
            new FixedAuthSecurityClock(Now.UtcDateTime));
        _commands.Clear();

        var result = await sut.GetOverviewAsync(scope);

        var january = result.Trend.Single(point => point.Month == "2026-01");
        january.Expenses.Should().Be(150m);
        january.Net.Should().Be(-150m);
        result.Trend.Single(point => point.Month == "2025-12").Expenses.Should().Be(0m);

        _commands.Should().ContainSingle();
        _commands[0].Should().Contain("expense.\"Status\" = 2");
        _commands[0].Should().Contain("COALESCE(expense.\"PaidAt\", expense.\"IncurredAt\")");
    }

    [Fact]
    public async Task GetOverviewAsync_WithoutReportsReadCapabilityReturnsNoPropertyMetrics()
    {
        var property = SeedProperty(
            "Leasing Property", "303", WorkOrderPriority.Normal, 500m, 1_000m, 1_000m, 0m);
        var scope = SeedSelectedPropertyScope(property.Id, RoleProfileKeys.LeasingAgent);
        await _ctx.Db.SaveChangesAsync();

        var sut = new AnalyticsService(
            _ctx.Db,
            new FixedTimeProvider(Now),
            new FixedAuthSecurityClock(Now.UtcDateTime));
        _commands.Clear();

        var result = await sut.GetOverviewAsync(scope);

        result.TotalUnits.Should().Be(0);
        result.OpenWorkOrders.Should().BeEmpty();
        result.Trend.Should().HaveCount(12);
        result.Trend.Should().OnlyContain(point =>
            point.Income == 0m && point.Expenses == 0m && point.Net == 0m);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task GetOverviewAsync_UsesSecurityClockForAccessChecksAndBusinessClockForMetrics()
    {
        var property = SeedProperty(
            "Simulation Property", "404", WorkOrderPriority.High, 125m, 1_200m, 1_050m, 400m);
        var scope = SeedSelectedPropertyScope(property.Id, RoleProfileKeys.WorkspaceAdministrator);
        await _ctx.Db.SaveChangesAsync();

        var simulatedBusinessNow = new DateTimeOffset(2027, 1, 18, 12, 0, 0, TimeSpan.Zero);
        await FreezeDatabaseBusinessClockAsync(simulatedBusinessNow);
        var sut = new AnalyticsService(
            _ctx.Db,
            new FixedTimeProvider(simulatedBusinessNow),
            new FixedAuthSecurityClock(Now.UtcDateTime));
        _commands.Clear();

        var result = await sut.GetOverviewAsync(scope);

        result.TotalUnits.Should().Be(1);
        result.OccupiedUnits.Should().Be(1);
        result.MonthRentScheduled.Should().Be(0m);
        result.MonthRentCollected.Should().Be(0m);
        result.MonthlyRecurringRent.Should().Be(0m, "the fixed-term agreement ended before the simulated business date");
        result.OpenWorkOrders.Should().ContainSingle();
        result.Trend.Should().Contain(point => point.Month == "2027-01");
        _commands.Should().ContainSingle();
        _commands[0].Should().Contain("authorized_properties");
        _commands[0].Should().Contain(AnalyticsService.AuthorizationSource);
        _commands[0].Should().NotContain("AuthSessions");
        _commands[0].Should().NotContain("WorkspaceMemberships");
        _commands[0].Should().NotContain("MembershipRoleAssignments");
        _commands[0].Should().Contain("generate_series");
    }

    private Property SeedProperty(
        string name,
        string unitNumber,
        WorkOrderPriority priority,
        decimal expenseAmount,
        decimal ledgerRentAmount,
        decimal governingRentAmount,
        decimal collectedAmount)
    {
        var now = Now.UtcDateTime;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{name} address",
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
            MarketRent = ledgerRentAmount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = $"{name} repair",
            Description = "Authorization boundary proof",
            Priority = priority,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Unit,
            Property = property,
            Unit = unit,
            Description = $"{name} expense",
            Status = ExpenseStatus.Paid,
            Amount = expenseAmount,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.AddRange(property, unit, workOrder, expense);
        _ctx.Db.SaveChanges();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{unitNumber}",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(relationship);
        _ctx.Db.SaveChanges();

        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Analytics",
            LastName = unitNumber,
            Email = $"analytics-{unitNumber}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Analytics fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{unitNumber}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var termStart = DateOnly.FromDateTime(now.AddMonths(-1));
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{unitNumber}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = termStart,
            TermEndOn = DateOnly.FromDateTime(now.AddDays(20)),
            GoverningFromOn = termStart,
            BaseRentAmount = governingRentAmount,
            RentDueDay = 1,
            SecurityDepositObligation = governingRentAmount,
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
        _ctx.Db.AddRange(party, account, agreement);
        _ctx.Db.SaveChanges();

        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email,
            SigningOrder = 1,
            IsRequired = true,
        });
        _ctx.Db.SaveChanges();

        var (issuedArtifact, executedArtifact) = SeedAgreementArtifacts(relationship.Id, now);
        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now.AddMonths(-1);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now.AddMonths(-1);
        _ctx.Db.SaveChanges();

        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = ledgerRentAmount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2026, 7, 1),
            DueOn = new DateOnly(2026, 7, 1),
            PostedAtUtc = now,
            Description = "July rent",
            BusinessKey = $"rent:2026-07:{unitNumber}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.TenantLedgerEntries.Add(charge);
        _ctx.Db.SaveChanges();

        if (collectedAmount > 0m)
        {
            var receipt = new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = PortfolioId,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Amount = collectedAmount,
                Currency = "USD",
                EffectiveOn = new DateOnly(2026, 7, 5),
                PostedAtUtc = now,
                Description = "July payment",
                BusinessKey = $"receipt:2026-07:{unitNumber}",
                CreatedByUserId = ActorUserId,
            };
            _ctx.Db.TenantLedgerEntries.Add(receipt);
            _ctx.Db.SaveChanges();
            _ctx.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
            {
                PortfolioId = PortfolioId,
                TenantAccountId = account.Id,
                DebitEntryId = charge.Id,
                CreditEntryId = receipt.Id,
                Amount = collectedAmount,
                AllocatedAtUtc = now,
                BusinessKey = $"allocation:2026-07:{unitNumber}",
                CreatedByUserId = ActorUserId,
            });
            _ctx.Db.SaveChanges();
        }

        return property;
    }

    private async Task FreezeDatabaseBusinessClockAsync(DateTimeOffset businessNow)
    {
        var clock = await _ctx.Db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = businessNow.UtcDateTime;
        clock.RealAnchorUtc = businessNow.UtcDateTime;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = businessNow.UtcDateTime;
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
    }

    private (LegalDocumentArtifact Issued, LegalDocumentArtifact Executed) SeedAgreementArtifacts(
        int relationshipId,
        DateTime now)
    {
        var issuedFile = AgreementFile($"agreement-{relationshipId}-issued.pdf", now);
        var executedFile = AgreementFile($"agreement-{relationshipId}-executed.pdf", now);
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

    private static StoredFile AgreementFile(string fileName, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = now,
    };

    private WorkspaceReadScope SeedSelectedPropertyScope(int propertyId, string roleKey)
    {
        var now = Now.UtcDateTime;
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
                role.Key == roleKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SelectedProperties =
            [
                new MembershipRoleAssignmentProperty
                {
                    PortfolioId = PortfolioId,
                    PropertyId = propertyId,
                },
            ],
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
        return new WorkspaceReadScope(
            PortfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedAuthSecurityClock(DateTime utcNow) : IAuthSecurityClock
    {
        public DateTime UtcNow() => utcNow;
    }

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

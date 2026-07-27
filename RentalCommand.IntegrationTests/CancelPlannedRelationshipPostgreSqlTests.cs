using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class CancelPlannedRelationshipPostgreSqlTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<CancelPlannedRelationshipResult> CancelCodec =
        new("lease-management.cancel-planned.v1");
    private static readonly AtomicJsonResultCodec<TenantLedgerMutationResult> LedgerCodec =
        new("tenant-account.ledger.mutation.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public CancelPlannedRelationshipPostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<CancelPlannedRelationshipCommand,
            CancelPlannedRelationshipResult, CancelPlannedRelationshipHandler>();
        services.AddAtomicCommandHandler<ReverseTenantLedgerEntryCommand,
            TenantLedgerMutationResult, ReverseTenantLedgerEntryHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task CancelPlannedRelationship_ReturnsFinancialBlockThenSucceedsAfterLedgerReversal()
    {
        var scenario = await SeedScenarioAsync("opening-balance");
        var openingBalance = await AddLedgerEntryAsync(
            scenario, TenantLedgerEntryType.OpeningBalance, TenantLedgerDirection.Debit, 250m);

        var blockedCommand = CancelCommand(scenario, "before-reversal");
        var blocked = await Atomic.ExecuteAsync(CancelIdentity(blockedCommand), blockedCommand, CancelCodec);

        blocked.Value.Outcome.Should().Be(CancelPlannedRelationshipOutcome.FinancialResolutionRequired);

        var reversalCommand = ReverseLedger(scenario, openingBalance.Id, "opening-balance");
        var reversal = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.ledger.reverse", reversalCommand.DeliveryIdempotencyKey),
            reversalCommand,
            LedgerCodec);
        reversal.Value.Applied.Should().BeTrue();
        reversal.Value.ReversesEntryId.Should().Be(openingBalance.Id);

        var cancelCommand = CancelCommand(scenario, "after-reversal");
        var canceled = await Atomic.ExecuteAsync(CancelIdentity(cancelCommand), cancelCommand, CancelCodec);

        canceled.Value.Outcome.Should().Be(CancelPlannedRelationshipOutcome.Canceled);
        canceled.Value.AccountClosedAtUtc.Should().NotBeNull();
        _context.Db.ChangeTracker.Clear();
        var relationship = await _context.Db.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.LeaseManagementId);
        var account = await _context.Db.TenantAccounts.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.TenantAccountId);
        relationship.CanceledAtUtc.Should().NotBeNull();
        account.ClosedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelPlannedRelationship_BlocksNetZeroMoneyWithUnreversedAllocation()
    {
        var scenario = await SeedScenarioAsync("allocation");
        var debit = await AddLedgerEntryAsync(
            scenario, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit, 75m);
        var credit = await AddLedgerEntryAsync(
            scenario, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit, 75m);
        _context.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.TenantAccountId,
            DebitEntryId = debit.Id,
            CreditEntryId = credit.Id,
            Amount = 75m,
            AllocatedAtUtc = DateTime.UtcNow,
            BusinessKey = $"allocation:{Guid.NewGuid():N}",
            CreatedByUserId = scenario.ActorUserId,
        });
        await _context.Db.SaveChangesAsync();

        var command = CancelCommand(scenario, "unreversed-allocation");
        var result = await Atomic.ExecuteAsync(CancelIdentity(command), command, CancelCodec);

        result.Value.Outcome.Should().Be(CancelPlannedRelationshipOutcome.FinancialResolutionRequired);
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        var actor = await _context.Db.Users.SingleAsync(user => user.Id == 1);
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Cancel Planned PostgreSQL {suffix} {Guid.NewGuid():N}",
            AddressLine1 = "754 Cancellation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = $"cancel-{Guid.NewGuid():N}"[..12],
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(property, unit);
        await _context.Db.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-CANCEL-{Guid.NewGuid():N}"[..32],
            PlannedPossessionAtUtc = now.AddDays(14),
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(relationship);
        await _context.Db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-CANCEL-{Guid.NewGuid():N}"[..32],
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _context.Db.TenantAccounts.Add(account);
        await _context.Db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
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
            User = actor,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();

        return new Scenario(1, property.Id, unit.Id, relationship.Id, account.Id, actor.Id,
            session.Id, context.Id, context.AccessRevision, DateOnly.FromDateTime(now));
    }

    private async Task<TenantLedgerEntry> AddLedgerEntryAsync(
        Scenario scenario,
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction,
        decimal amount)
    {
        var entry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.TenantAccountId,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = scenario.BusinessDate,
            DueOn = entryType is TenantLedgerEntryType.RentCharge
                or TenantLedgerEntryType.AddendumCharge
                or TenantLedgerEntryType.LateFeeCharge
                or TenantLedgerEntryType.DepositCharge
                or TenantLedgerEntryType.ManualCharge
                    ? scenario.BusinessDate
                    : null,
            PostedAtUtc = DateTime.UtcNow,
            Description = $"{entryType} cancellation test",
            BusinessKey = $"cancel-planned:{entryType}:{Guid.NewGuid():N}",
            CreatedByUserId = scenario.ActorUserId,
        };
        _context.Db.TenantLedgerEntries.Add(entry);
        await _context.Db.SaveChangesAsync();
        return entry;
    }

    private static CancelPlannedRelationshipCommand CancelCommand(Scenario scenario, string suffix) => new(
        scenario.PortfolioId,
        scenario.LeaseManagementId,
        scenario.UnitId,
        scenario.ActorUserId,
        scenario.SessionId,
        scenario.AccessContextId,
        scenario.AccessRevision,
        "ManualCorrection",
        null,
        "Canceled before possession during integration proof.",
        [],
        $"cancel-planned:{suffix}:{Guid.NewGuid():N}");

    private static ReverseTenantLedgerEntryCommand ReverseLedger(
        Scenario scenario,
        long entryId,
        string suffix) => new(
        scenario.PortfolioId,
        scenario.TenantAccountId,
        entryId,
        scenario.BusinessDate,
        $"Reverse opening balance {suffix}",
        null,
        scenario.ActorUserId,
        scenario.SessionId,
        scenario.AccessContextId,
        scenario.AccessRevision,
        CapabilityKeys.MoneyChargesManage,
        $"tenant-ledger-reversal:{suffix}:{Guid.NewGuid():N}",
        $"tenant-ledger-reversal:{scenario.PortfolioId}:{scenario.TenantAccountId}:{entryId}:{suffix}:{Guid.NewGuid():N}");

    private IAtomicUnitOfWork Atomic => _services.GetRequiredService<IAtomicUnitOfWork>();

    private static AtomicCommandIdentity CancelIdentity(CancelPlannedRelationshipCommand command) => new(
        "lease-management.cancel-planned",
        $"{command.PortfolioId}:{command.LeaseManagementId}:{command.DeliveryIdempotencyKey}");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:cancel-planned-postgresql";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int PropertyId,
        int UnitId,
        int LeaseManagementId,
        int TenantAccountId,
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        DateOnly BusinessDate);
}

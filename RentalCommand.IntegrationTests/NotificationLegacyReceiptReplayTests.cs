using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class NotificationLegacyReceiptReplayTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BatchToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // Frozen legacy fingerprints computed from the command DTO shapes at 461c3a1b. These values
    // must never be regenerated from the current command model or a current serialization helper.
    private const string NotificationFingerprint = "ad8d2aacab586e7324b61e07143c30f5c1526b4406bb0e652124f9f212e456b0";
    private const string DraftFingerprint = "64f47c54cb1e5519b569439b7e8fc06a4bd908d2a780b2300fab8060b69a1ef9";
    private const string DeliveryFingerprint = "8b775a42cd9d79f9d240a5c8d57ce6af249c4d2d52a1943b905f0c2a5515ff26";
    private const string OwnerFingerprint = "aa085192d11b89389ee22bf4a9a2cf3cbd323ba437bdadc83b83a1b883abfb52";
    private const string BatchFingerprint = "a665fa6f34715b086cb27446dd300f20205a3ca98b67414b4357111ee5edf9b6";
    private const string BriefingFingerprint = "a78057567ee54ed6936625fd530547da368f628730cc72be88baa61069ae5093";
    private const string ForbiddenNotificationFingerprint = "284d77f9249e4882e5e6d6a1df5a6608311a7e83bcf938accfc1527a23533051";

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public NotificationLegacyReceiptReplayTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task FrozenLegacyReceiptsReplayThroughRealAuthorizationWithoutExecutingWrites()
    {
        await SeedReplayAuthorityAsync();
        await using var serviceScope = _services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var requests = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var jobs = serviceScope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>();

        var notification = new AtomicNotificationMutationCommand(
            1, 1, SessionId, 9, 1, AtomicNotificationMutationDomain.Broadcast, 0, string.Empty,
            "{\"title\":\"Legacy\"}", "legacy-notification-delivery ");
        await ReplayRequestAsync(requests, "rental.notification.broadcast",
            "1:9:Broadcast:0::legacy-notification-delivery ", notification,
            NotificationFingerprint,
            "{\"Found\":true,\"Applied\":true,\"EntityId\":71,\"AffectedCount\":1,\"ResponseJson\":\"{\\u0022id\\u0022:71}\"}",
            new AtomicNotificationMutationResult(true, true, 71, 1, "{\"id\":71}"),
            "rental.notification-mutation.v1", AtomicNotificationMutation.Write(db, notification));

        var draft = new AtomicNoticeDraftMutationCommand(
            1, 1, SessionId, 9, 1, AtomicNoticeDraftOperation.Generate, 0, "{}", "legacy-draft-delivery");
        await ReplayRequestAsync(requests, "rental.notice-draft.generate",
            "1:9:Generate:0:legacy-draft-delivery", draft, DraftFingerprint,
            "{\"Found\":true,\"Applied\":false,\"NoticeDraftId\":0,\"CreatedCount\":0,\"ResponseJson\":\"{}\"}",
            new AtomicNoticeDraftMutationResult(true, false, 0, 0, "{}"),
            "rental.notice-draft-mutation.v1", AtomicNoticeDraftMutation.Write(db, draft));

        var delivery = new AtomicNoticeDeliveryCommand(
            1, null, null, null, null, 73, [NoticeDeliveryChannel.Email], 91, WorkToken,
            "legacy-notice-delivery");
        await ReplayRequestAsync(requests, "rental.notice-delivery.approve",
            "1:73:legacy-notice-delivery", delivery, DeliveryFingerprint,
            "{\"RenderedNoticeId\":74,\"NoticeDraftId\":73,\"DeliveryCount\":1}",
            new AtomicNoticeDeliveryResult(74, 73, 1),
            "rental.notice-delivery.v1", AtomicNoticeDelivery.Write(db, delivery));

        var owner = new QueueOwnerStatementEmailCommand(
            1, 1, SessionId, 9, 1, 75, 2026, "owner@example.test", "Legacy statement", "Legacy body",
            "legacy-owner-statement");
        await ReplayRequestAsync(requests, "owner-statement.email.queue",
            "1:9:75:2026:legacy-owner-statement", owner, OwnerFingerprint,
            "{\"Queued\":true,\"Reason\":null}", new QueueOwnerStatementEmailResult(true),
            "owner-statement.email.queue.v1", QueueOwnerStatementEmail.Write(db, owner));

        var batch = new ApplyClaimedTenantNoticeDraftBatchCommand(BatchToken);
        var batchHandler = new ApplyClaimedTenantNoticeDraftBatchRule(db);
        await ReplayJobAsync(jobs, "tenant-notice-draft.claimed-batch.apply",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", batch, BatchFingerprint,
            "{\"CreatedCount\":0,\"Drafts\":[]}", new ApplyClaimedTenantNoticeDraftBatchResult(0, []),
            "tenant-notice-draft.claimed-batch.apply.v1",
            TenantNoticeDraftAutomation.Write(batch, batchHandler.ExecuteAsync, batchHandler.AuthorizeAsync));

        var briefing = new EnqueueMorningBriefingsCommand(Now);
        await ReplayJobAsync(jobs, "notifications.morning-briefing.enqueue", "utc-hour:2026082112",
            briefing, BriefingFingerprint, "{\"QueuedCount\":6}", new EnqueueMorningBriefingsResult(6),
            "notifications.morning-briefing.enqueue.v1", DailyBriefingDeliveryService.Write(db, briefing));

        (await _context.Db.Notifications.CountAsync()).Should().Be(0);
        (await _context.Db.NoticeDrafts.CountAsync()).Should().Be(0);
        (await _context.Db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task FrozenLegacyReceiptReplay_RefusesMissingWorkspaceAuthority()
    {
        await SeedReplayAuthorityAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var command = new AtomicNotificationMutationCommand(
            1, 999, SessionId, 9, 1, AtomicNotificationMutationDomain.Broadcast, 0, string.Empty,
            "{\"title\":\"Forbidden\"}", "forbidden-notification");
        var key = "1:9:Broadcast:0::forbidden-notification";
        await SeedReceiptAsync("rental.notification.broadcast", key, ForbiddenNotificationFingerprint,
            "rental.notification-mutation.v1",
            "{\"Found\":true,\"Applied\":true,\"EntityId\":72,\"AffectedCount\":1,\"ResponseJson\":null}");
        var action = () => scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteExactAsync(key, ReplayOnly(AtomicNotificationMutation.Write(db, command)));

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private async Task ReplayRequestAsync<TCommand, TResult>(
        IRequestWriteExecutor executor, string operation, string key, TCommand command,
        string fingerprint, string resultJson, TResult stored, string contract,
        TransactionalWrite<TCommand, TResult> productionWrite)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        await SeedReceiptAsync(operation, key, fingerprint, contract, resultJson);
        productionWrite.OperationName.Should().Be(operation);
        productionWrite.ResultContract.Should().Be(contract);
        var replay = await executor.ExecuteExactAsync(key, ReplayOnly(productionWrite));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    private async Task ReplayJobAsync<TCommand, TResult>(
        IJobStepWriteExecutor executor, string operation, string key, TCommand command,
        string fingerprint, string resultJson, TResult stored, string contract,
        TransactionalWrite<TCommand, TResult> productionWrite)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        await SeedReceiptAsync(operation, key, fingerprint, contract, resultJson);
        productionWrite.OperationName.Should().Be(operation);
        productionWrite.ResultContract.Should().Be(contract);
        var replay = await executor.ExecuteAsync(key, ReplayOnly(productionWrite));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    private async Task SeedReceiptAsync(
        string operation, string key, string fingerprint, string contract, string resultJson)
    {
        _context.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = operation,
            IdempotencyKey = key, RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract,
            ResultJson = resultJson, StartedAt = Now, CompletedAt = Now,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task SeedReplayAuthorityAsync()
    {
        var db = _context.Db;
        var user = await db.Users.SingleAsync(row => row.Id == 1);
        var access = new WorkspaceAccessContext
        {
            Id = 9, UserId = 1, PortfolioId = 1, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = access, PortfolioId = 1, Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = Now.AddMinutes(-5),
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = Now.AddMinutes(-5), CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var session = new AuthSession
        {
            Id = SessionId, User = user, ActiveAccessContext = access, Status = AuthSessionStatus.Active,
            CreatedAtUtc = Now, LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddYears(20),
        };
        var property = new Property
        {
            Id = 76, PortfolioId = 1, Name = "Legacy replay property", AddressLine1 = "1 Replay Way",
            City = "Akron", State = "OH", PostalCode = "44301", CreatedAt = Now, UpdatedAt = Now,
        };
        var owner = new OwnerEntity
        {
            Id = 75, PortfolioId = 1, Name = "Legacy Owner", Email = "owner@example.test",
            CreatedAt = Now, UpdatedAt = Now,
        };
        var ownership = new PropertyOwnership
        {
            PortfolioId = 1, Property = property, OwnerEntity = owner, OwnershipSharePercent = 100,
            EffectiveFromUtc = Now.AddYears(-1), StatementRecipientName = owner.Name,
            StatementRecipientEmail = owner.Email, PayeeName = owner.Name,
        };
        var unit = new Unit
        {
            Id = 77, PortfolioId = 1, Property = property, UnitNumber = "1",
            Bedrooms = 1, Bathrooms = 1, MarketRent = 1000, CreatedAt = Now, UpdatedAt = Now,
        };
        var tenant = new Tenant
        {
            Id = 78, PortfolioId = 1, FirstName = "Legacy", LastName = "Tenant",
            CreatedAt = Now, UpdatedAt = Now,
        };
        var management = new LeaseManagement
        {
            Id = 79, PublicId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            PortfolioId = 1, Property = property, Unit = unit, RelationshipNumber = "LM-LEGACY-REPLAY",
            PossessionGivenAtUtc = Now.AddMonths(-1), CreatedAtUtc = Now, UpdatedAtUtc = Now,
            CreatedByUserId = 1, RowVersion = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        };
        var party = new LeaseManagementParty
        {
            Id = 80, PortfolioId = 1, LeaseManagement = management, Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant, EffectiveFrom = new DateOnly(2026, 1, 1),
            ChangeReason = "Legacy replay", CreatedAtUtc = Now, CreatedByUserId = 1,
        };
        var systemTemplateId = await db.SystemNoticeTemplateVersions.Select(row => row.Id).FirstAsync();
        var template = new WorkspaceNoticeTemplateVersion
        {
            Id = 81, PortfolioId = 1, SystemKey = "legacy-replay", Version = 1,
            BasedOnSystemTemplateVersionId = systemTemplateId, Subject = "Legacy", Body = "Legacy",
            CreatedByUserId = 1, CreatedAtUtc = Now,
        };
        var policy = new TenantNoticePolicy
        {
            Id = 82, PortfolioId = 1, AutomationKey = "legacy-replay", Mode = TenantNoticeMode.Draft,
            WorkspaceNoticeTemplateVersionId = 81, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var work = ClaimedWork(91, 82, 79, 80, "legacy-replay-work", WorkToken);
        var batchWork = ClaimedWork(92, 82, 79, 80, "legacy-replay-batch", BatchToken);

        db.AddRange(assignment, session, ownership, unit, tenant, party, template, policy, work, batchWork);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static TenantNoticeWorkItem ClaimedWork(
        long id, int policyId, int managementId, int partyId, string key, Guid token) => new()
    {
        Id = id, PortfolioId = 1, TenantNoticePolicyId = policyId, LeaseManagementId = managementId,
        RecipientLeaseManagementPartyId = partyId, DueAtUtc = Now.AddMinutes(-1),
        Status = TenantNoticeWorkStatus.Claimed, BusinessKey = key, AttemptCount = 1,
        ClaimOwner = "legacy-worker", ClaimToken = token,
        ClaimExpiresAtUtc = Now.AddMinutes(-1), CreatedAtUtc = Now,
    };

    private static TransactionalWrite<TCommand, TResult> ReplayOnly<TCommand, TResult>(
        TransactionalWrite<TCommand, TResult> write)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull => new(
            write.OperationName, write.Request, write.ResultContract,
            write.LockPlan,
            (_, _, _) => throw new InvalidOperationException("A frozen receipt must not execute."),
            write.AuthorizeReplayAsync);
}

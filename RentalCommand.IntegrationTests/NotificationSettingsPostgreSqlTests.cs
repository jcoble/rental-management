using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Notifications;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class NotificationSettingsPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public NotificationSettingsPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task SettingsQuery_JoinsRecipientsTemplatesAndPagesInPostgreSql()
    {
        var supplied = await _context.Db.SystemNoticeTemplateVersions.AsNoTracking()
            .OrderBy(template => template.Id)
            .Select(template => new
            {
                template.Id,
                template.SystemKey,
                template.Version,
                template.JurisdictionCode,
            })
            .ToListAsync();
        supplied.Select(template => template.Id).Should().Contain([4, 5, 6, 7, 8, 9]);
        supplied.Single(template => template.Id == 6).Version.Should().Be(2);
        supplied.Single(template => template.Id == 7).Version.Should().Be(2);
        supplied.Single(template => template.Id == 8).Version.Should().Be(3);
        supplied.Single(template => template.Id == 9).Version.Should().Be(3);
        supplied.Where(template => template.Id is 6 or 7 or 8 or 9)
            .Should().OnlyContain(template => template.JurisdictionCode == null);

        var latestPerKey =
            from candidate in _context.Db.SystemNoticeTemplateVersions.AsNoTracking()
            where !_context.Db.SystemNoticeTemplateVersions.Any(newer =>
                newer.SystemKey == candidate.SystemKey &&
                newer.Version > candidate.Version)
            orderby candidate.SystemKey
            select new { candidate.Id, candidate.SystemKey, candidate.Version };

        var settingsPage =
            (from policy in _context.Db.TenantNoticePolicies.AsNoTracking()
             join template in _context.Db.WorkspaceNoticeTemplateVersions.AsNoTracking()
                 on new { TemplateId = policy.WorkspaceNoticeTemplateVersionId, policy.PortfolioId }
                 equals new { TemplateId = template.Id, template.PortfolioId }
             join system in _context.Db.SystemNoticeTemplateVersions.AsNoTracking()
                 on template.BasedOnSystemTemplateVersionId equals system.Id
             from party in _context.Db.LeaseManagementParties.AsNoTracking()
                 .Where(party => party.PortfolioId == policy.PortfolioId)
                 .DefaultIfEmpty()
             from tenant in _context.Db.Tenants.AsNoTracking()
                 .Where(tenant => party != null &&
                     tenant.PortfolioId == party.PortfolioId &&
                     tenant.Id == party.TenantId)
                 .DefaultIfEmpty()
             orderby policy.AutomationKey, party!.Id
             select new
             {
                 policy.AutomationKey,
                 policy.Mode,
                 TemplateVersion = template.Version,
                 SystemVersion = system.Version,
                 RecipientEmail = tenant == null ? null : tenant.Email,
             })
            .Take(20);

        var latestSql = CaptureSql(latestPerKey);
        var settingsSql = CaptureSql(settingsPage);
        latestSql.Should().Contain("NOT EXISTS")
            .And.Contain("ORDER BY");
        var latest = await latestPerKey.ToListAsync();
        latest.Where(template => template.SystemKey is "lease-non-renewal" or "late-rent-late-fee")
            .Should().OnlyContain(template =>
                template.Version == 3 && (template.Id == 8 || template.Id == 9));
        settingsSql.Should().Contain("JOIN")
            .And.Contain("LEFT JOIN")
            .And.Contain("ORDER BY")
            .And.Contain("LIMIT");
    }

    [Fact]
    public async Task FrozenDeliveryEvidence_PreservesTemplateVersion()
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "notice-evidence@example.test",
            NormalizedUserName = "NOTICE-EVIDENCE@EXAMPLE.TEST",
            Email = "notice-evidence@example.test",
            NormalizedEmail = "NOTICE-EVIDENCE@EXAMPLE.TEST",
            DisplayName = "Notice Evidence",
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = "Notice Evidence Workspace",
            ManagementCompanyName = "Notice Evidence Workspace",
            Status = PortfolioStatus.Active,
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(user, portfolio);
        await _context.Db.SaveChangesAsync();

        var v1 = SuppliedNoticeTemplateBaseline.V1.Single(template =>
            template.SystemKey == "lease-non-renewal");
        var v2 = SuppliedNoticeTemplateBaseline.V2Legal.Single(template =>
            template.SystemKey == "lease-non-renewal");
        var workspaceV1 = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = portfolio.Id,
            SystemKey = v1.SystemKey,
            Version = 1,
            BasedOnSystemTemplateVersionId = v1.Id,
            Subject = v1.Subject,
            Body = v1.Body,
            CreatedByUserId = user.Id,
            CreatedAtUtc = now,
        };
        _context.Db.WorkspaceNoticeTemplateVersions.Add(workspaceV1);
        await _context.Db.SaveChangesAsync();

        var frozen = new RenderedNotice
        {
            PortfolioId = portfolio.Id,
            NoticeDraftId = 91001,
            WorkspaceNoticeTemplateVersionId = workspaceV1.Id,
            LeaseManagementId = 92001,
            Subject = workspaceV1.Subject,
            Body = workspaceV1.Body,
            ContentSha256 = new string('a', 64),
            TemplateProvenance = SuppliedNoticeTemplateBaseline.Provenance,
            RenderedAtUtc = now,
        };
        _context.Db.RenderedNotices.Add(frozen);
        await _context.Db.SaveChangesAsync();

        _context.Db.WorkspaceNoticeTemplateVersions.Add(new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = portfolio.Id,
            SystemKey = v2.SystemKey,
            Version = 2,
            BasedOnSystemTemplateVersionId = v2.Id,
            Subject = v2.Subject,
            Body = v2.Body,
            CreatedByUserId = user.Id,
            CreatedAtUtc = now.AddMinutes(1),
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var preserved = await _context.Db.RenderedNotices.AsNoTracking()
            .SingleAsync(notice => notice.Id == frozen.Id);
        preserved.WorkspaceNoticeTemplateVersionId.Should().Be(workspaceV1.Id);
        preserved.Subject.Should().Be(v1.Subject);
        preserved.Body.Should().Be(v1.Body);
        preserved.ContentSha256.Should().Be(new string('a', 64));
        preserved.TemplateProvenance.Should().Be(SuppliedNoticeTemplateBaseline.Provenance);
    }

    [Fact]
    public async Task MyAlertsRequestExecutorPreservesLegacyFingerprintAndCompanionRows()
    {
        var seededAt = new DateTime(2027, 1, 21, 12, 0, 0, DateTimeKind.Utc);
        var businessNow = new DateTime(2027, 1, 22, 5, 0, 0, DateTimeKind.Utc);
        var sessionId = Guid.Parse("57e8e27d-e54d-47ba-9d20-e8c70e11fd39");
        await using var database = await _fixture.CreateContextAsync();
        var scope = await SeedAdministratorScopeAsync(database.Db, seededAt, sessionId);
        await SeedAlertPreferenceAsync(database.Db, scope, seededAt);
        var actor = await database.Db.Users.AsNoTracking().SingleAsync(user => user.Id == scope.UserId);
        var request = new UpdateMyAlertsRequest(
            EnableInApp: true,
            EnableMobilePush: false,
            EnableEmail: true,
            EnableSms: true);
        const string operationKey = "phase1-my-alerts-canary";
        await using (var services = BuildAtomicServices(
            database.ConnectionString, new FixedTimeProvider(businessNow), new OutboxFailureInterceptor()))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<NotificationFoundationService>();
            var response = await sut.UpdateMyAlertsAsync(scope, request, operationKey, default);
            response.Should().Be(new MyAlertsResponse(
                scope.UserId,
                actor.DisplayName,
                actor.Email!,
                actor.PhoneNumber,
                true,
                false,
                true,
                true));
        }

        var identity = new AtomicCommandIdentity(
            "rental.notification.myalerts",
            $"{scope.PortfolioId}:{scope.AccessContextId}:MyAlerts:0::{operationKey}");
        var receipt = await database.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var legacyFingerprint = AtomicCommandFingerprint.Create(new LegacyNotificationMutationFingerprint(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            0,
            0,
            string.Empty,
            System.Text.Json.JsonSerializer.Serialize(request),
            operationKey,
            businessNow));
        receipt.RequestFingerprint.Should().Be(legacyFingerprint);
        receipt.ResultContract.Should().Be("rental.notification-mutation.v1");
        var rows = await ReadCanaryRowsAsync(
            database.Db, identity, operationKey, receipt.AttemptId);
        rows.AttemptCorrelation.ReceiptMatchesAuditMatchesOutcomeAttempt.Should().BeTrue();
        rows.Audit.Should().BeEquivalentTo(new
        {
            CommandType = "rental.notification.myalerts",
            CommandIdempotencyKey = identity.IdempotencyKey,
            MutationOrdinal = 1L,
            PortfolioId = scope.PortfolioId,
            UserId = (int?)scope.UserId,
            EntityType = nameof(UserAlertPreference),
            Operation = AuditLogOperation.Updated,
            ChangeReason = "Personal notification destinations updated",
            Timestamp = businessNow,
        }, options => options.ExcludingMissingMembers());
        rows.Outbox.Should().BeEquivalentTo(new
        {
            PortfolioId = (int?)scope.PortfolioId,
            MessageType = "data-update",
            IdempotencyKey = operationKey + ":data-update",
            AttemptCount = 0,
            CreatedAtUtc = businessNow,
            NextAttemptAtUtc = businessNow,
        }, options => options.ExcludingMissingMembers());
        rows.Preference.Should().BeEquivalentTo(new
        {
            PortfolioId = scope.PortfolioId,
            UserId = scope.UserId,
            EnableInApp = true,
            EnableMobilePush = false,
            EnableEmail = true,
            EnableSms = true,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = businessNow,
        }, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public async Task DeviceRegistration_PreservesLegacyRows_ReplaysExactly_AndRejectsStaleAuthorization()
    {
        var seededAt = new DateTime(2027, 1, 23, 10, 0, 0, DateTimeKind.Utc);
        var scope = await SeedAdministratorScopeAsync(seededAt);
        const string token = "phase3-device-token";
        const string operationKey = "phase3-device-register";
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant()[..24];

        await using (var services = BuildAtomicServices(
            new FixedTimeProvider(seededAt), new OutboxFailureInterceptor()))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<DeviceService>();
            await sut.RegisterAsync(scope, token, "android", operationKey);
            await sut.RegisterAsync(scope, token, "android", operationKey);
        }

        _context.Db.ChangeTracker.Clear();
        var device = await _context.Db.DeviceTokens.AsNoTracking().SingleAsync(row =>
            row.PortfolioId == scope.PortfolioId && row.Token == token);
        device.UserId.Should().Be(scope.UserId);
        device.Platform.Should().Be("android");
        device.LastSeenAt.Should().Be(device.CreatedAt);
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == "rental.notification.deviceregister"
            && row.IdempotencyKey ==
                $"{scope.PortfolioId}:{scope.AccessContextId}:DeviceRegister:0:{tokenHash}:{operationKey}");
        receipt.Status.Should().Be(AtomicCommandReceiptStatus.Completed);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(
            new LegacyNotificationMutationFingerprint(
                scope.PortfolioId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                8,
                0,
                tokenHash,
                System.Text.Json.JsonSerializer.Serialize(new { Token = token, Platform = "android" }),
                operationKey,
                default)));
        receipt.ResultContract.Should().Be("rental.notification-mutation.v1");
        System.Text.Json.JsonSerializer.Deserialize<AtomicNotificationMutationResult>(receipt.ResultJson!)
            .Should().Be(new AtomicNotificationMutationResult(true, true, device.Id, 1));
        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == "rental.notification.deviceregister"
            && row.CommandIdempotencyKey == receipt.IdempotencyKey
            && row.EntityType == nameof(DeviceToken)
            && row.EntityId == device.Id
            && row.Operation == AuditLogOperation.Created
            && row.ChangeReason == "Push notification device registered"
            && row.AttemptId == receipt.AttemptId);
        audit.Timestamp.Should().Be(device.CreatedAt);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == operationKey + ":data-update")).Should().Be(0);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = seededAt.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using (var services = BuildAtomicServices(
            new FixedTimeProvider(seededAt.AddMinutes(2)), new OutboxFailureInterceptor()))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<DeviceService>();
            Func<Task> staleReplay = () => sut.RegisterAsync(
                scope, token, "android", operationKey);
            await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("Workspace access changed. Refresh and try again.");
        }

        (await _context.Db.DeviceTokens.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId && row.Token == token)).Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == "rental.notification.deviceregister"
            && row.CommandIdempotencyKey == receipt.IdempotencyKey)).Should().Be(1);
    }

    [Fact]
    public async Task MyAlertsAtomicWrite_UsesBusinessClockForCompanions_ReplaysAndRollsBack()
    {
        var seededAt = DateTime.UtcNow;
        var scope = await SeedAdministratorScopeAsync(seededAt);
        var businessNow = new DateTime(2027, 1, 22, 5, 0, 0, DateTimeKind.Utc);
        var outboxFailure = new OutboxFailureInterceptor();
        await using var services = BuildAtomicServices(new FixedTimeProvider(businessNow), outboxFailure);
        await using var runScope = services.CreateAsyncScope();
        var sut = runScope.ServiceProvider.GetRequiredService<NotificationFoundationService>();
        var request = new UpdateMyAlertsRequest(
            EnableInApp: true,
            EnableMobilePush: true,
            EnableEmail: true,
            EnableSms: false);

        var saved = await sut.UpdateMyAlertsAsync(scope, request, "ys169-freeze", default);

        saved.EnableInApp.Should().BeTrue();
        saved.EnableMobilePush.Should().BeTrue();
        saved.EnableEmail.Should().BeTrue();
        saved.EnableSms.Should().BeFalse();
        _context.Db.ChangeTracker.Clear();
        var preference = await _context.Db.UserAlertPreferences.AsNoTracking()
            .SingleAsync(row => row.PortfolioId == scope.PortfolioId && row.UserId == scope.UserId);
        preference.CreatedAtUtc.Should().Be(businessNow);
        preference.UpdatedAtUtc.Should().Be(businessNow);
        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking()
            .SingleAsync(row => row.CommandType == "rental.notification.myalerts"
                && row.CommandIdempotencyKey.EndsWith(":ys169-freeze")
                && row.EntityType == nameof(UserAlertPreference));
        audit.Timestamp.Should().Be(businessNow);
        var outbox = await _context.Db.OutboxMessages.AsNoTracking()
            .SingleAsync(row => row.IdempotencyKey == "ys169-freeze:data-update");
        outbox.CreatedAtUtc.Should().Be(businessNow);
        outbox.NextAttemptAtUtc.Should().Be(businessNow);

        var replayClock = new FixedTimeProvider(businessNow.AddDays(1));
        await using (var replayServices = BuildAtomicServices(replayClock, new OutboxFailureInterceptor()))
        await using (var replayScope = replayServices.CreateAsyncScope())
        {
            var replay = replayScope.ServiceProvider.GetRequiredService<NotificationFoundationService>();
            var replayed = await replay.UpdateMyAlertsAsync(scope, request, "ys169-freeze", default);

            replayed.Should().BeEquivalentTo(saved);
        }

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.UserAlertPreferences.AsNoTracking()
            .CountAsync(row => row.PortfolioId == scope.PortfolioId && row.UserId == scope.UserId))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row => row.CommandType == "rental.notification.myalerts"
                && row.CommandIdempotencyKey.EndsWith(":ys169-freeze")))
            .Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == "ys169-freeze:data-update"))
            .Should().Be(1);

        outboxFailure.FailOutboxInsert = true;
        Func<Task> fail = async () => await sut.UpdateMyAlertsAsync(
            scope, request with { EnableSms = true }, "ys169-rollback", default);

        await fail.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.UserAlertPreferences.AsNoTracking()
            .CountAsync(row => row.PortfolioId == scope.PortfolioId && row.UserId == scope.UserId
                && row.EnableSms))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row => row.CommandType == "rental.notification.myalerts"
                && row.CommandIdempotencyKey.EndsWith(":ys169-rollback")))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row => row.CommandType == "rental.notification.myalerts"
                && row.IdempotencyKey.EndsWith(":ys169-rollback")))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == "ys169-rollback:data-update"))
            .Should().Be(0);
    }

    private static string CaptureSql<T>(IQueryable<T> query) =>
        query.ToQueryString();

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(DateTime now)
        => await SeedAdministratorScopeAsync(_context.Db, now, Guid.NewGuid());

    private static async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(
        RentalCommandDbContext db,
        DateTime now,
        Guid sessionId)
    {
        var user = await db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
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
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = sessionId,
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };

        db.AddRange(accessContext, membership, assignment, session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new WorkspaceReadScope(
            1,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private ServiceProvider BuildAtomicServices(
        TimeProvider timeProvider,
        OutboxFailureInterceptor outboxFailure) =>
        BuildAtomicServices(_context.ConnectionString, timeProvider, outboxFailure);

    private static ServiceProvider BuildAtomicServices(
        string connectionString,
        TimeProvider timeProvider,
        OutboxFailureInterceptor outboxFailure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(outboxFailure);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<NotificationFoundationService>();
        services.AddScoped<DeviceService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<OutboxFailureInterceptor>()));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task SeedAlertPreferenceAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime now)
    {
        db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = scope.PortfolioId,
            UserId = scope.UserId,
            EnableInApp = false,
            EnableMobilePush = true,
            EnableEmail = false,
            EnableSms = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<CanaryRows> ReadCanaryRowsAsync(
        RentalCommandDbContext db,
        AtomicCommandIdentity identity,
        string operationKey,
        Guid outcomeAttemptId)
    {
        db.ChangeTracker.Clear();
        var receipt = await db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var audit = await db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == operationKey + ":data-update");
        var preference = await db.UserAlertPreferences.AsNoTracking().SingleAsync(row =>
            row.PortfolioId == 1 && row.UserId == 1);

        receipt.AttemptId.Should().NotBeEmpty();
        audit.AttemptId.Should().NotBeEmpty();
        receipt.AttemptId.Should().Be(outcomeAttemptId);
        audit.AttemptId.Should().Be(outcomeAttemptId);

        return new CanaryRows(
            new AttemptCorrelationRow("<run-attempt>", "<run-attempt>",
                receipt.AttemptId == audit.AttemptId && audit.AttemptId == outcomeAttemptId),
            new ReceiptRow(receipt.CommandType, receipt.IdempotencyKey, receipt.RequestFingerprint,
                receipt.Status, receipt.ResultContract, receipt.ResultJson, receipt.StartedAt,
                receipt.CompletedAt),
            new AuditRow(audit.CommandType, audit.CommandIdempotencyKey, audit.MutationOrdinal,
                audit.PortfolioId, audit.UserId, audit.ActorLabel, audit.EntityType, audit.EntityId,
                audit.Operation, audit.OldValues, audit.NewValues, audit.ChangeReason, audit.Timestamp,
                audit.IpAddress),
            new OutboxRow(outbox.PortfolioId, outbox.MessageType, outbox.Payload,
                outbox.IdempotencyKey, outbox.AttemptCount, outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc, outbox.LastAttemptAtUtc, outbox.ClaimOwner,
                outbox.ClaimToken, outbox.ClaimExpiresAtUtc, outbox.AcceptedAtUtc,
                outbox.DeliveredAtUtc, outbox.DeadLetteredAtUtc, outbox.Provider,
                outbox.ProviderMessageId, outbox.FailureKind, outbox.LastError),
            new PreferenceRow(preference.Id, preference.PortfolioId, preference.UserId,
                preference.EnableInApp, preference.EnableMobilePush, preference.EnableEmail,
                preference.EnableSms, preference.CreatedAtUtc, preference.UpdatedAtUtc));
    }

    private sealed record CanaryRows(
        AttemptCorrelationRow AttemptCorrelation,
        ReceiptRow Receipt,
        AuditRow Audit,
        OutboxRow Outbox,
        PreferenceRow Preference);

    private sealed record AttemptCorrelationRow(
        string ReceiptAttemptId,
        string AuditAttemptId,
        bool ReceiptMatchesAuditMatchesOutcomeAttempt);

    private sealed record ReceiptRow(
        string CommandType,
        string IdempotencyKey,
        string RequestFingerprint,
        AtomicCommandReceiptStatus Status,
        string ResultContract,
        string? ResultJson,
        DateTime StartedAt,
        DateTime? CompletedAt);

    private sealed record AuditRow(
        string CommandType,
        string CommandIdempotencyKey,
        long MutationOrdinal,
        int PortfolioId,
        int? UserId,
        string? ActorLabel,
        string EntityType,
        int EntityId,
        AuditLogOperation Operation,
        string? OldValues,
        string? NewValues,
        string? ChangeReason,
        DateTime Timestamp,
        string? IpAddress);

    private sealed record OutboxRow(
        int? PortfolioId,
        string MessageType,
        string Payload,
        string IdempotencyKey,
        int AttemptCount,
        DateTime CreatedAtUtc,
        DateTime NextAttemptAtUtc,
        DateTime? LastAttemptAtUtc,
        string? ClaimOwner,
        Guid? ClaimToken,
        DateTime? ClaimExpiresAtUtc,
        DateTime? AcceptedAtUtc,
        DateTime? DeliveredAtUtc,
        DateTime? DeadLetteredAtUtc,
        string? Provider,
        string? ProviderMessageId,
        OutboxFailureKind? FailureKind,
        string? LastError);

    private sealed record PreferenceRow(
        int Id,
        int PortfolioId,
        int UserId,
        bool EnableInApp,
        bool EnableMobilePush,
        bool EnableEmail,
        bool EnableSms,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record LegacyNotificationMutationFingerprint(
        int PortfolioId,
        int ActorUserId,
        [property: AtomicFingerprintIgnore]
        Guid AuthSessionId,
        [property: AtomicFingerprintIgnore]
        int AccessContextId,
        [property: AtomicFingerprintIgnore]
        long ExpectedAccessRevision,
        int Domain,
        int EntityId,
        string ResourceKey,
        string RequestJson,
        [property: AtomicFingerprintIgnore]
        string DeliveryIdempotencyKey,
        [property: AtomicFingerprintIgnore]
        DateTime BusinessNowUtc) : IAtomicCommandData;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:my-alerts";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class OutboxFailureInterceptor : DbCommandInterceptor
    {
        public bool FailOutboxInsert { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedOutboxFailure : Exception;
}

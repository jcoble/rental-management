using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
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
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };

        _context.Db.AddRange(accessContext, membership, assignment, session);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        return new WorkspaceReadScope(
            1,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private ServiceProvider BuildAtomicServices(
        TimeProvider timeProvider,
        OutboxFailureInterceptor outboxFailure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(outboxFailure);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicNotificationMutationCommand,
            AtomicNotificationMutationResult,
            AtomicNotificationMutationHandler>();
        services.AddScoped<NotificationFoundationService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(
                    _context.ConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 2))
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<OutboxFailureInterceptor>()));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

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

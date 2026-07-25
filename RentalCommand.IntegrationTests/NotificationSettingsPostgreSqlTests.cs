using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
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
        supplied.Select(template => template.Id).Should().Contain([4, 5, 6, 7]);
        supplied.Single(template => template.Id == 6).Version.Should().Be(2);
        supplied.Single(template => template.Id == 7).Version.Should().Be(2);
        supplied.Where(template => template.Id is 6 or 7)
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

    private static string CaptureSql<T>(IQueryable<T> query) =>
        query.ToQueryString();
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Translation-only proof for canonical tenant-notice eligibility. No database connection is made.
/// </summary>
public sealed class NoticeDraftSqlTranslationTests
{
    [Fact]
    public void ActiveNoticeContext_IsOneServerSideCanonicalRelationshipStatement()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
                .Options);
        var service = new NoticeDraftService(
            db,
            Mock.Of<ILlmProvider>(),
            NullLogger<NoticeDraftService>.Instance,
            TimeProvider.System);

        var sql = service.ActiveNoticeContextQuery(17).ToQueryString();

        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("Tenants");
        sql.Should().Contain("CurrentAgreementId");
        sql.Should().Contain("CurrentPrimaryTenantId");
        sql.Should().Contain("EffectiveFrom");
        sql.Should().Contain("EffectiveThrough");
        sql.Should().Contain("PrimaryTenant");
        sql.Should().NotContain("NoticeDrafts\".\"LeaseId");
        sql.Should().NotContain("NoticeDrafts\".\"PaymentId");
    }
}

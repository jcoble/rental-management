using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Listings;
using RentalCommand.Data.Leasing;
using RentalCommand.Data.Listings;

namespace RentalCommand.Data.Tests;

public sealed class WorkspaceAuthorizationConsumerSqlTranslationTests
{
    private static readonly Guid SessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime SecurityNowUtc =
        new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Leasing_and_listing_authorization_consumers_translate_as_single_statements()
    {
        using var db = Context();

        var agreementSql = LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                new ReplaceIssuedAgreementWithDraftCommand(
                    17, 42, 73, null, "Correction", 5, SessionId, 12, 3, "agreement-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var partySql = LeasePartyAccessCommandSupport.AuthorizedRelationships(
                new GrantTenantUserAccessCommand(
                    17, 42, 9, "Resident access", "https://example.test", 5,
                    SessionId, 12, 3, "party-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var moveInSql = PrepareMoveInHandler.AuthorizedUnits(
                new PrepareMoveInCommand(
                    17, null, 42, 5, SessionId, 12, 3, null,
                    new DateOnly(2026, 9, 1), [], null, default,
                    new DateOnly(2026, 9, 1), null, 1500m, 1, 1500m, 0m, 0,
                    1, "{}", false, null, null, null, "move-in-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var dispositionSql = CreatePropertyDispositionHandler.AuthorizedProperties(
                new CreatePropertyDispositionCommand(
                    17, 9, SecurityNowUtc, 250000m, 15000m, null, null, 5,
                    SessionId, 12, 3, "disposition-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var listingSql = ListingWorkspaceCommandSupport.AuthorizedListings(
                new GenerateListingWorkspaceCommand(17, 42, 5, SessionId, 12, 3),
                db,
                SecurityNowUtc)
            .ToQueryString();

        foreach (var sql in new[] { agreementSql, partySql, moveInSql, dispositionSql, listingSql })
        {
            sql.Should().Contain("MembershipRoleAssignments");
            sql.Should().Contain("WorkspaceMemberships");
            sql.Should().Contain("WorkspaceAccessContexts");
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("RoleProfileCapabilities");
            sql.Should().Contain("MembershipRoleAssignmentProperties");
            sql.Should().Contain("AllProperties");
            sql.Should().Contain("SelectedProperties");
            sql.Should().NotContain("ClientEvaluation");
            sql.TrimEnd().Should().NotEndWith(";",
                "the complete authorization consumer must render as one SQL statement");
        }

        agreementSql.Should().Contain("LeaseManagements");
        agreementSql.Should().Contain("rentals.manage");
        agreementSql.Should().Contain("leasing.agreements.prepare");
        partySql.Should().Contain("LeaseManagements");
        partySql.Should().Contain("rentals.manage");
        partySql.Should().Contain("leasing.onboarding.manage");
        moveInSql.Should().Contain("Units");
        moveInSql.Should().Contain("rentals.manage");
        moveInSql.Should().Contain("leasing.agreements.prepare");
        dispositionSql.Should().Contain("Properties");
        dispositionSql.Should().Contain("rentals.manage");
        listingSql.Should().Contain("Units");
        listingSql.Should().Contain("RentalListings");
        listingSql.Should().Contain("leasing.listings.manage");
    }

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);
}

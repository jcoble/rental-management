using FluentAssertions;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class TenantNoticeCandidateGenerationServiceTests
{
    [Fact]
    public void CandidateSql_UsesOnlyCanonicalLeaseAndNoticeSources()
    {
        var sql = TenantNoticeCandidateGenerationService.CandidateInsertSql;

        sql.Should().Contain("\"TenantNoticePolicies\"");
        sql.Should().Contain("\"TenantNoticeWorkItems\"");
        sql.Should().Contain("\"LeaseManagements\"");
        sql.Should().Contain("\"LeaseAgreements\"");
        sql.Should().Contain("\"TenantAccounts\"");
        sql.Should().Contain("\"vw_lease_management_lifecycle\"");
        sql.Should().Contain("\"vw_lease_agreement_status\"");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ExpiryReminderSentAt");
        sql.Should().NotContain("LeaseStatus");
    }

    [Fact]
    public void CandidateSql_RequiresExplicitDispositionAndEligibleRecipientChannel()
    {
        var sql = TenantNoticeCandidateGenerationService.CandidateInsertSql;

        sql.Should().Contain("OfferRenewal");
        sql.Should().Contain("OfferMonthToMonth");
        sql.Should().Contain("NonRenewalMoveOut");
        sql.Should().Contain("IncludePrimaryTenant");
        sql.Should().Contain("IncludeCoTenant");
        sql.Should().Contain("IncludeEligibleGuarantor");
        sql.Should().Contain("GuarantorLegalNoticeEligible");
        sql.Should().Contain("IncludeOccupant");
        sql.Should().Contain("SendEmail");
        sql.Should().Contain("SendSms");
        sql.Should().Contain("SendTenantPortal");
        sql.Should().Contain("SendMobilePush");
        sql.Should().Contain("TenantUserAccesses");
        sql.Should().Contain("WorkspaceAccessContexts");
        sql.Should().Contain("DeviceTokens");
        sql.Should().Contain("party.\"Id\" AS \"RecipientLeaseManagementPartyId\"");
        sql.Should().Contain(":party:");
        sql.Should().Contain("policy.\"Classification\" = 'Legal'");
        sql.Should().Contain("policy.\"AutomationKey\" NOT IN ('rent-reminder', 'late-rent-late-fee')");
    }

    [Fact]
    public void CandidateSql_ComputesDueTimeAndDeduplicatesInsidePostgres()
    {
        var sql = TenantNoticeCandidateGenerationService.CandidateInsertSql;

        sql.Should().Contain("AT TIME ZONE");
        sql.Should().Contain("LeadDays");
        sql.Should().Contain("SendHourLocal");
        sql.Should().Contain("EffectiveNowUtc");
        sql.Should().Contain("ON CONFLICT (\"BusinessKey\") DO NOTHING");
        sql.Should().Contain("clock_timestamp()");
    }

    [Fact]
    public void CandidateSql_UsesCanonicalChargeProjectionForExactRentAndLateWork()
    {
        var sql = TenantNoticeCandidateGenerationService.CandidateInsertSql;

        sql.Should().Contain("\"vw_tenant_charge_balances\"");
        sql.Should().Contain("'rent-reminder'");
        sql.Should().Contain("'late-rent-late-fee'");
        sql.Should().Contain("'RentCharge'");
        sql.Should().Contain("'LateFeeCharge'");
        sql.Should().Contain("charge.\"OpenAmount\" > 0");
        sql.Should().Contain("charge.\"IsPastDue\"");
        sql.Should().Contain("row_number() OVER");
        sql.Should().Contain("\"TenantLedgerEntryId\"");
        sql.Should().Contain(":ledger:");
        sql.Should().Contain("\"RecipientLeaseManagementPartyId\", \"TenantLedgerEntryId\"");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Translation-only proofs for the clean-replacement readers. These tests never open a database
/// connection; Npgsql must be able to render each full query as one SQL statement over the
/// canonical relationship projections.
/// </summary>
public sealed class CanonicalLeaseReaderSqlTests
{
    [Fact]
    public void Unit_health_reader_joins_occupancy_lifecycle_and_governing_agreement_in_sql()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildHealthQuery(17)
            .OrderBy(row => row.UnitNumber)
            .Skip(20)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Basic_unit_reader_derives_presentation_status_from_canonical_occupancy()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildCanonicalResponseQuery(17)
            .OrderBy(unit => unit.UnitNumber)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("IsOccupied");
        sql.Should().Contain("HasScheduledMoveIn");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseStatus");
    }

    [Fact]
    public void Unit_delete_guard_is_one_canonical_database_projection()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildDeletionGuardQuery(17, 42).ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("EXISTS");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseStatus");
    }

    [Fact]
    public void Property_delete_guards_are_canonical_database_projections()
    {
        using var db = NewContext();
        var service = new PropertyService(db, Mock.Of<IDataUpdateService>(), TimeProvider.System);

        var propertySql = service.BuildPropertyDeletionGuardQuery(17, 9).ToQueryString();
        var canonicalUnitSql = service.BuildUnitDeletionGuardQuery(17, 42).ToQueryString();
        var canonicalUnitResponseSql = service.BuildCanonicalUnitResponseQuery(17, 42).ToQueryString();

        foreach (var sql in new[] { propertySql, canonicalUnitSql })
        {
            sql.Should().Contain("vw_unit_occupancy");
            sql.Should().Contain("vw_lease_management_lifecycle");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("EXISTS");
            sql.Should().NotContain("\"Leases\"");
            sql.Should().NotContain("LeaseStatus");
        }

        canonicalUnitResponseSql.Should().Contain("vw_unit_occupancy");
        canonicalUnitResponseSql.Should().NotContain("\"Leases\"");
    }

    [Fact]
    public void Unit_mutation_contracts_do_not_accept_source_status()
    {
        typeof(CreateUnitRequest).GetProperty("Status").Should().BeNull();
        typeof(UpdateUnitRequest).GetProperty("Status").Should().BeNull();
    }

    [Fact]
    public void Tenant_relationship_counts_are_distinct_database_aggregates_over_canonical_records()
    {
        using var db = NewContext();
        var service = new TenantService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<ITenantPortalProvisioningService>(),
            NullLogger<TenantService>.Instance,
            TimeProvider.System);

        var sql = service.BuildRelationshipCountQuery(
                db.Tenants.AsNoTracking().Where(tenant => tenant.PortfolioId == 17),
                17)
            .OrderBy(row => row.Entity.LastName)
            .Take(25)
            .ToQueryString();

        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("count");
        sql.Should().Contain("DISTINCT");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Unit_dashboard_header_joins_canonical_occupancy_agreement_and_account_in_one_statement()
    {
        using var db = NewContext();
        var service = new UnitDashboardService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            TimeProvider.System);

        var sql = service.BuildCanonicalDashboardQuery(17, 42).ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("TenantAccountId");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Tenant_account_ledger_header_authorizes_and_aggregates_in_one_canonical_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildCanonicalLedgerHeaderQuery(ReadAccess(), 42)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("money.balances.read");
        sql.Should().Contain("MembershipRoleAssignments");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagements");
        sql.Should().NotContain("LeaseManagementParties",
            "staff capability authorization is membership/property scoped, not tenant-party scoped");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("count");
        sql.Should().NotContain("\"Payments\"");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("OpeningBalances");
    }

    [Fact]
    public void Tenant_account_ledger_entries_sort_and_page_in_the_database()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildCanonicalLedgerEntriesQuery(17, 81)
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.Id)
            .Skip(20)
            .Take(10)
            .ToQueryString();

        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("TenantPaymentAttempts");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Payments\"");
        sql.Should().NotContain("OpeningBalances");
    }

    [Fact]
    public void Tenant_account_ledger_response_carries_exact_account_and_entry_route()
    {
        var entry = new LeaseManagementQueryService.CanonicalLedgerEntryReadRow
        {
            Id = 812,
            TenantAccountId = 42,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1200m,
            EffectiveOn = new DateOnly(2026, 7, 13),
            Description = "July rent",
        };
        var header = new LeaseManagementQueryService.CanonicalLedgerHeaderReadRow
        {
            TenantAccountId = 42,
            PropertyId = 7,
            PropertyName = "Maple Ridge",
        };

        var response = LeaseManagementQueryService.ToLedgerResponse(entry, header);

        response.TenantAccountId.Should().Be(42);
        response.SourceHref.Should().Be("/tenant-accounts/42/entries/812");
    }

    [Fact]
    public void Lease_management_page_authorizes_filters_sorts_and_pages_canonical_rows_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);
        var query = new LeaseManagementListQuery
        {
            PropertyId = 91,
            TenantId = 27,
            Lifecycle = "PossessionActive",
            Search = "mallard",
            Sort = "-tenantName",
            Skip = 20,
            Take = 10,
        };

        var sql = service.BuildSummaryQuery(ReadAccess(), query)
            .OrderByDescending(row => row.PrimaryTenantName)
            .ThenByDescending(row => row.LeaseManagementId)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Lease_management_detail_parties_are_authorized_and_projected_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var headerSql = service.BuildDetailHeaderQuery(ReadAccess(), 42).ToQueryString();
        var partySql = service.BuildPartyQuery(ReadAccess(), 42)
            .OrderBy(row => row.Role)
            .ThenBy(row => row.TenantName)
            .ToQueryString();

        foreach (var sql in new[] { headerSql, partySql })
        {
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("rentals.read");
            sql.Should().Contain("LeaseManagements");
            sql.Should().NotContain("\"Leases\"");
            sql.Should().NotContain("LeaseTenants");
        }
        partySql.Should().Contain("LeaseManagementParties");
        partySql.Should().Contain("Tenants");
        partySql.Should().Contain("ORDER BY");
        partySql.Should().NotContain("TenantUserAccesses",
            "ordinary lease detail must not carry the return-possession access collection");
    }

    [Fact]
    public void Return_possession_context_uses_two_flat_translated_queries_without_keyless_collection_correlation()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var partySql = service.BuildCurrentPartiesQuery(ReadAccess(), 42).ToQueryString();
        var accessSql = service.BuildCurrentPartyAccessQuery(ReadAccess(), 42).ToQueryString();

        foreach (var sql in new[] { partySql, accessSql })
        {
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("rentals.read");
            sql.Should().Contain("vw_lease_management_lifecycle");
            sql.Should().Contain("BusinessDate");
            sql.Should().Contain("EffectiveFrom");
            sql.Should().Contain("EffectiveThrough");
            sql.Should().Contain("ORDER BY");
            sql.TrimEnd().Should().NotEndWith(";");
        }

        partySql.Should().Contain("LeaseManagementParties");
        partySql.Should().NotContain("TenantUserAccesses");
        accessSql.Should().Contain("LeaseManagementParties");
        accessSql.Should().Contain("TenantUserAccesses");
        accessSql.Should().Contain("AspNetUsers");
        accessSql.Should().Contain("RevokedAtUtc");
    }

    [Fact]
    public void Agreement_draft_detail_authorizes_and_projects_exact_edit_state_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAgreementDraftDetailQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentSourceVersions");
        sql.Should().Contain("LeaseAgreementSigners");
        sql.Should().Contain("DraftRevision");
        sql.Should().Contain("TermsSchemaVersion");
        sql.Should().Contain("TermsPayload");
        sql.Should().Contain("DocumentTemplateId");
        sql.Should().Contain("SigningOrder");
        sql.Should().Contain("IssuedAtUtc\" IS NULL");
        sql.Should().Contain("DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Agreement_signature_progress_authorizes_and_projects_packet_signers_and_artifact_readiness_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAgreementSignatureProgressQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("SignatureRequests");
        sql.Should().Contain("SignatureSigners");
        sql.Should().Contain("LeaseAgreementSigners");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("SigningOrder");
        sql.Should().Contain("SignedAtUtc");
        sql.Should().Contain("DeclinedAtUtc");
        sql.Should().Contain("DeletedAt");
        sql.Should().Contain("count");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("TokenHash");
        sql.Should().NotContain("TokenExpiresAtUtc");
        sql.Should().NotContain("ProviderEnvelopeId");
        sql.Should().NotContain("LastError");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Renewal_addendum_series_authorizes_and_resolves_exact_current_effective_versions_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildEffectiveAddendumSeriesQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("IsGoverning");
        sql.Should().Contain("BusinessDate");
        sql.Should().Contain("LeaseAddenda");
        sql.Should().Contain("LeaseAddendumFinancialEffects");
        sql.Should().Contain("SeriesPublicId");
        sql.Should().Contain("VersionNumber");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("ExecutedArtifactId");
        sql.Should().Contain("EffectiveFromOn");
        sql.Should().Contain("EffectiveThroughOn");
        sql.Should().Contain("SupersededEffectiveOn");
        sql.Should().Contain("NOT EXISTS");
        sql.Should().Contain("count");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Addendum_draft_detail_authorizes_and_projects_exact_edit_state_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAddendumDraftDetailQuery(ReadAccess(), 42, 74)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAddenda");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentSourceVersions");
        sql.Should().Contain("LeaseAddendumSigners");
        sql.Should().Contain("LeaseAddendumFinancialEffects");
        sql.Should().Contain("DraftRevision");
        sql.Should().Contain("TermsSchemaVersion");
        sql.Should().Contain("TermsPayload");
        sql.Should().Contain("DocumentTemplateId");
        sql.Should().Contain("ReplacesAddendumId");
        sql.Should().Contain("SigningOrder");
        sql.Should().Contain("EffectType");
        sql.Should().Contain("ChargeCode");
        sql.Should().Contain("IssuedAtUtc\" IS NULL");
        sql.Should().Contain("DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Agreement_history_authorizes_filters_sorts_and_pages_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);
        var query = new LeaseLegalHistoryQuery
        {
            Status = "Active",
            Search = "A-2026",
            Skip = 10,
            Take = 20,
        };

        var sql = service.BuildAgreementHistoryQuery(ReadAccess(), 42, query)
            .OrderByDescending(row => row.VersionNumber)
            .ThenByDescending(row => row.LeaseAgreementId)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("LeaseAgreementSigners");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("EntityType");
        sql.Should().Contain("EntityId");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Addendum_history_authorizes_filters_sorts_and_pages_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);
        var query = new LeaseLegalHistoryQuery
        {
            Status = "Executed",
            Search = "PET",
            Skip = 5,
            Take = 15,
        };

        var sql = service.BuildAddendumHistoryQuery(ReadAccess(), 42, query)
            .OrderByDescending(row => row.EffectiveFromOn)
            .ThenByDescending(row => row.LeaseAddendumId)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAddenda");
        sql.Should().Contain("vw_lease_addendum_status");
        sql.Should().Contain("LeaseAddendumFinancialEffects");
        sql.Should().Contain("LeaseAddendumSigners");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
    }

    [Fact]
    public void Addendum_base_agreement_selector_filters_sorts_and_pages_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAddendumEligibleBaseAgreementQuery(ReadAccess(), 42)
            .OrderByDescending(row => row.GoverningFromOn)
            .ThenByDescending(row => row.VersionNumber)
            .ThenByDescending(row => row.LeaseAgreementId)
            .Skip(20)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("ExecutedArtifactId");
        sql.Should().Contain("VoidedAtUtc");
        sql.Should().Contain("DraftCanceledAtUtc");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
    }

    [Fact]
    public void Legal_artifact_download_resolves_exact_management_agreement_and_file_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var agreementSql = service.BuildAgreementArtifactFileQuery(ReadAccess(), 42, 73, 91)
            .ToQueryString();
        var addendumSql = service.BuildAddendumArtifactFileQuery(ReadAccess(), 42, 74, 92)
            .ToQueryString();

        foreach (var sql in new[] { agreementSql, addendumSql })
        {
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("rentals.read");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("LegalDocumentArtifacts");
            sql.Should().Contain("StoredFiles");
            sql.Should().NotContain("EntityType");
            sql.Should().NotContain("\"Leases\"");
        }
        agreementSql.Should().Contain("LeaseAgreements");
        addendumSql.Should().Contain("LeaseAddenda");
    }

    [Fact]
    public void Lease_qa_facts_are_admitted_by_the_same_authorized_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildLeaseQaAgreementQuery(ReadAccess(), 42).ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("AccessRevision");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Legal_issue_signers_are_exact_target_property_authorized_server_side_queries()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var agreementSql = service
            .BuildAuthorizedAgreementIssueSignerQuery(ReadAccess(), 42, 73)
            .ToQueryString();
        var addendumSql = service
            .BuildAuthorizedAddendumIssueSignerQuery(ReadAccess(), 42, 74)
            .ToQueryString();

        foreach (var sql in new[] { agreementSql, addendumSql })
        {
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("AccessRevision");
            sql.Should().Contain("rentals.manage");
            sql.Should().Contain("leasing.agreements.prepare");
            sql.Should().Contain("MembershipRoleAssignmentProperties");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("ORDER BY");
            sql.Should().NotContain("ClientEvaluation");
        }

        agreementSql.Should().Contain("LeaseAgreements");
        agreementSql.Should().Contain("LeaseAgreementSigners");
        addendumSql.Should().Contain("LeaseAddenda");
        addendumSql.Should().Contain("LeaseAddendumSigners");
    }

    [Fact]
    public void Agreement_source_scan_uses_the_typed_stored_file_authority_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAgreementSourceScanFileQuery(ReadAccess(), 42, 73)
            .OrderByDescending(row => row.UploadedAtUtc)
            .Take(1)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("LeaseAgreement");
        sql.Should().Contain("EntityType");
        sql.Should().Contain("EntityId");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("\"Leases\"");
    }

    [Fact]
    public void Scan_originated_tenant_money_activity_resolves_account_label_and_unit_canonically()
    {
        using var db = NewContext();
        var dashboard = new DashboardService(db, new AuditDescriber(), TimeProvider.System);
        var audit = new AuditQueryService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            Mock.Of<RentalCommand.Core.Time.IAppTimeZoneProvider>(),
            TimeProvider.System);

        var dashboardSql = dashboard.BuildRecentActivityProjectionQuery(ReadScope())
            .ToQueryString();
        var auditSql = audit.BuildPageProjectionQuery(
                ReadScope(),
                operation: null,
                entityType: nameof(RentalCommand.Core.Entities.TenantAccount),
                entityId: null,
                query: new ListQuery { Take = 20 })
            .ToQueryString();

        foreach (var sql in new[] { dashboardSql, auditSql })
        {
            sql.Should().Contain("TenantAccounts");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("UnitId");
            sql.Should().NotContain("\"Payments\"");
            sql.Should().NotContain("\"Leases\"");
        }

        dashboardSql.Should().Contain("AuditLogs");
        dashboardSql.Should().Contain("AuthSessions");
        dashboardSql.Should().Contain("reports.read");
        dashboardSql.Should().Contain("AccountNumber");
        auditSql.Should().Contain("AuditLogs");
        auditSql.Should().Contain("LIMIT");
    }

    private static LeaseManagementQueryService NewLeaseManagementQueryService(RentalCommandDbContext db) =>
        new(db, TimeProvider.System);

    private static LeaseManagementReadContext ReadAccess() =>
        new(17, 5, Guid.Parse("77777777-7777-7777-7777-777777777777"), 12, 3);

    private static RentalCommand.Core.Authorization.WorkspaceReadScope ReadScope() =>
        new(17, 5, Guid.Parse("77777777-7777-7777-7777-777777777777"), 12, 3);

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}

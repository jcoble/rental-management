using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Policies;
using RentalCommand.Data.Screening;
using RentalCommand.Data.Vendors;

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
            TimeProvider.System,
            Mock.Of<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());

        var sql = service.BuildHealthQuery(17)
            .OrderBy(row => row.UnitNumber)
            .Skip(20)
            .Take(20)
            .ToQueryString();
        var documentSql = service.BuildUnitDocumentCountsQuery(17, [42, 43])
            .ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("WorkOrders");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("StoredFiles");

        documentSql.Should().Contain("LegalDocumentArtifacts");
        documentSql.Should().Contain("StoredFiles");
        documentSql.Should().Contain("UNION");
        documentSql.Should().Contain("GROUP BY");
        documentSql.Should().NotContain("\"Leases\"");
        documentSql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Basic_unit_reader_derives_presentation_status_from_canonical_occupancy()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());

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
        var sql = db.UnitDeleteEligibility(17, 42).ToQueryString();

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

        var propertySql = db.PropertyDeleteEligibility(17, 9).ToQueryString();
        var canonicalUnitSql = db.UnitDeleteEligibility(17, 42).ToQueryString();
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
    public void Extracted_write_policies_remain_composable_database_queries()
    {
        using var db = NewContext();
        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

        var authorizationSql = db.Properties.AsNoTracking()
            .WhereAuthorizedForScope(db, ReadScope(), CapabilityKeys.RentalsManage, now)
            .ToQueryString();
        var applicationSql = db.RentalApplications
            .OpenForEmail(17, "resident@example.test")
            .ToQueryString();
        var resolutionSql = db.ResolvePropertyUnit(17, 9, 42).ToQueryString();
        var tenantDeleteSql = db.TenantDeleteEligibility(17, 5).ToQueryString();
        var w9Sql = RequestVendorW9Handler.AuthorizedVendors(
                new RequestVendorW9Command(
                    17, 8, "query-canary", 7, ReadScope().SessionId, ReadScope().UserId,
                    ReadScope().AccessContextId, ReadScope().AccessRevision, now),
                db,
                now)
            .ToQueryString();
        var screeningSql = ScreeningCommandSupport.AuthorizedApplications(
                17, 23, ReadScope().UserId, ReadScope().SessionId,
                ReadScope().AccessContextId, ReadScope().AccessRevision,
                db, now, tracking: false)
            .ToQueryString();
        var tenantService = new TenantService(
            db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        var tenantResponseSql = tenantService.BuildDeleteEligibilityQuery(
                db.Tenants.AsNoTracking().Where(tenant => tenant.PortfolioId == 17 && tenant.Id == 5),
                17)
            .ToQueryString();

        authorizationSql.Should().Contain("MembershipRoleAssignments");
        authorizationSql.Should().Contain("WorkspaceAccessContexts");
        authorizationSql.Should().Contain("AuthSessions");
        authorizationSql.Should().Contain("EXISTS");
        applicationSql.Should().Contain("RentalApplications");
        applicationSql.Should().Contain("lower");
        resolutionSql.Should().Contain("Properties");
        resolutionSql.Should().Contain("Units");
        tenantDeleteSql.Should().Contain("count");
        tenantDeleteSql.Should().Contain("DISTINCT");
        tenantDeleteSql.Should().Contain("vw_unit_occupancy");
        tenantDeleteSql.Should().Contain("vw_lease_management_lifecycle");
        foreach (var sql in new[] { w9Sql, screeningSql })
        {
            sql.Should().Contain("MembershipRoleAssignments");
            sql.Should().Contain("WorkspaceAccessContexts");
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("EXISTS");
        }
        w9Sql.Should().Contain("AllProperties");
        w9Sql.Should().Contain("Vendors");
        screeningSql.Should().Contain("RentalApplications");
        screeningSql.Should().Contain("MembershipRoleAssignmentProperties");
        tenantResponseSql.Should().Contain("count");
        tenantResponseSql.Should().Contain("DISTINCT");
        tenantResponseSql.Should().Contain("vw_unit_occupancy");
        tenantResponseSql.Should().Contain("vw_lease_management_lifecycle");
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

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("money.balances.read");
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

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("UpcomingAgreementNumber");
        sql.Should().Contain("EndingDispositionDecidedAtUtc");
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
            AssertCanonicalPropertyAuthorization(sql);
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
            sql.Should().Contain("rc_api_effective_capability_scopes");
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
        accessSql.Should().Contain("WorkspaceAccessContexts");
        accessSql.Should().Contain("WorkspaceMemberships");
        accessSql.Should().Contain("MembershipRoleAssignments");
        accessSql.Should().Contain("RoleProfiles");
        accessSql.Should().Contain(RoleProfileKeys.TenantPortal);
        accessSql.Should().Contain("WorkspaceInvitations");
        accessSql.Should().Contain("EffectiveNowUtc");
        accessSql.Should().Contain("RevokedAtUtc");
        accessSql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Agreement_draft_detail_authorizes_and_projects_exact_edit_state_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAgreementDraftDetailQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("rentals.read");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentSourceVersions");
        sql.Should().Contain("LeaseAgreementSigners");
        sql.Should().Contain("DraftRevision");
        sql.Should().Contain("TermsSchemaVersion");
        sql.Should().Contain("TermsPayload");
        sql.Should().Contain("DocumentTemplateId");
        sql.Should().Contain("ReplacesAgreementId");
        sql.Should().Contain("RenewsAgreementId");
        sql.Should().Contain("ReissuesAgreementId");
        sql.Should().Contain("TransferredFromAgreementId");
        sql.Should().Contain("SigningOrder");
        sql.Should().Contain("IssuedAtUtc\" IS NULL");
        sql.Should().Contain("DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Agreement_history_and_draft_detail_project_the_same_confirmed_source_scan_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);
        var query = new LeaseLegalHistoryQuery();

        var detailSql = service.BuildAgreementDraftDetailQuery(ReadAccess(), 42, 73)
            .ToQueryString();
        var historySql = service.BuildAgreementHistoryQuery(ReadAccess(), 42, query)
            .ToQueryString();

        foreach (var sql in new[] { detailSql, historySql })
        {
            sql.Should().Contain("ScanDrafts");
            sql.Should().Contain("TargetEntityType");
            sql.Should().Contain("ConfirmedEntityId");
            sql.Should().Contain("SourceStoredFileId");
            sql.Should().Contain("StoredFiles");
            sql.Should().Contain("EntityType");
            sql.Should().Contain("EntityId");
            sql.Should().Contain("DeletedAt");
            sql.TrimEnd().Should().NotEndWith(";");
            sql.Should().NotContain("ClientEvaluation");
        }
        detailSql.Should().Contain("FileName");
        detailSql.Should().Contain("ContentType");
    }

    [Fact]
    public void Agreement_signature_progress_authorizes_and_projects_packet_signers_and_artifact_readiness_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildAgreementSignatureProgressQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        AssertCanonicalPropertyAuthorization(sql);
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
    public void Party_legal_basis_picker_filters_searches_and_scopes_executed_agreements_in_sql()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildEligiblePartyLegalBasisQuery(
                ReadAccess(), 42, new ListQuery { Search = "name correction" })
            .OrderByDescending(item => item.FullyExecutedAtUtc)
            .Skip(5)
            .Take(20)
            .ToQueryString();

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("rentals.manage");
        sql.Should().Contain("leasing.agreements.prepare");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("ExecutedArtifactId");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
    }

    [Fact]
    public void Renewal_addendum_series_authorizes_and_resolves_exact_current_effective_versions_in_one_sql_statement()
    {
        using var db = NewContext();
        var service = NewLeaseManagementQueryService(db);

        var sql = service.BuildEffectiveAddendumSeriesQuery(ReadAccess(), 42, 73)
            .ToQueryString();

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("rentals.read");
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

        AssertCanonicalPropertyAuthorization(sql);
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

        AssertCanonicalPropertyAuthorization(sql);
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

        AssertCanonicalPropertyAuthorization(sql);
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

        AssertCanonicalPropertyAuthorization(sql);
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
            AssertCanonicalPropertyAuthorization(sql);
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

        AssertCanonicalPropertyAuthorization(sql);
        sql.Should().Contain("rentals.read");
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
            AssertCanonicalPropertyAuthorization(sql);
            sql.Should().Contain("rentals.manage");
            sql.Should().Contain("leasing.agreements.prepare");
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

        AssertCanonicalPropertyAuthorization(sql);
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

        var dashboardAuditSql = dashboard.BuildRecentActivityAuditPageQuery(ReadScope())
            .ToQueryString();
        var dashboardEntitySql = dashboard.BuildRecentActivityEntityFactQuery(
                ReadScope(),
                new Dictionary<string, long[]>(StringComparer.Ordinal)
                {
                    [nameof(RentalCommand.Core.Entities.TenantAccount)] = [42L],
                })
            .ToQueryString();
        var auditSql = audit.BuildPageProjectionQuery(
                ReadScope(),
                operation: null,
                entityType: nameof(RentalCommand.Core.Entities.TenantAccount),
                entityId: null,
                query: new ListQuery { Take = 20 })
            .ToQueryString();

        foreach (var sql in new[] { dashboardEntitySql, auditSql })
        {
            sql.Should().Contain("TenantAccounts");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("UnitId");
            sql.Should().NotContain("\"Payments\"");
            sql.Should().NotContain("\"Leases\"");
        }

        dashboardAuditSql.Should().Contain("AtomicAuditLogs");
        // This reader also carries the all-property assignment guard for unsupported/global audit rows.
        AssertCanonicalPropertyAuthorization(
            dashboardAuditSql,
            expectedCallCount: null,
            expectMembershipRoleAssignments: true);
        dashboardAuditSql.Should().Contain("reports.read");
        dashboardEntitySql.Should().Contain("AccountNumber");
        dashboardEntitySql.Should().Contain("= ANY");
        auditSql.Should().Contain("AtomicAuditLogs");
        auditSql.Should().Contain("LIMIT");
    }

    [Fact]
    public void Provider_dead_letter_search_stays_in_one_translated_audit_query()
    {
        using var db = NewContext();
        var audit = new AuditQueryService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            Mock.Of<RentalCommand.Core.Time.IAppTimeZoneProvider>(),
            TimeProvider.System);

        var sql = audit.BuildForensicPageProjectionQuery(
                portfolioId: 17,
                operation: null,
                entityType: nameof(RentalCommand.Core.Entities.TenantAccount),
                entityId: null,
                query: new ListQuery { Search = "evt_dead_99", Take = 20 })
            .ToQueryString();

        sql.Should().Contain("ChangeReason");
        sql.Should().Contain("NewValues");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("ToList");
    }

    [Fact]
    public void Dashboard_tenant_activity_facts_use_page_keys_and_one_ordered_party_aggregate()
    {
        using var db = NewContext();
        var dashboard = new DashboardService(db, new AuditDescriber(), TimeProvider.System);

        var sql = dashboard.BuildRecentActivityEntityFactQuery(
                ReadScope(),
                new Dictionary<string, long[]>(StringComparer.Ordinal)
                {
                    [nameof(RentalCommand.Core.Entities.Tenant)] = [42L, 43L],
                })
            .ToQueryString();

        sql.Should().Contain("Tenants");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("= ANY");
        sql.Should().Contain("array_agg");
        sql.Should().Contain("\"EffectiveFrom\" DESC");
        sql.Should().Contain("\"Id\" DESC");
        sql.Should().NotContain("max(",
            "the latest tenant party must not rebuild portfolio-wide latest-date/latest-id relations");
    }

    [Theory]
    [InlineData("Property", "Properties")]
    [InlineData("Unit", "Units")]
    [InlineData("LeaseManagement", "LeaseManagements")]
    [InlineData("LeaseAgreement", "LeaseAgreements")]
    [InlineData("LeaseAddendum", "LeaseAddenda")]
    [InlineData("TenantAccount", "TenantAccounts")]
    [InlineData("TenantLedgerEntry", "TenantLedgerEntries")]
    [InlineData("SecurityDepositAccount", "SecurityDepositAccounts")]
    [InlineData("SecurityDeposit", "SecurityDepositAccounts")]
    [InlineData("SecurityDepositEntry", "SecurityDepositEntries")]
    [InlineData("WorkOrder", "WorkOrders")]
    [InlineData("Expense", "Expenses")]
    [InlineData("Appointment", "Appointments")]
    [InlineData("Inspection", "Inspections")]
    [InlineData("RentalApplication", "RentalApplications")]
    public void Dashboard_activity_fact_branch_is_authorized_page_keyed_and_excludes_absent_types(
        string entityType,
        string expectedTable)
    {
        using var db = NewContext();
        var dashboard = new DashboardService(db, new AuditDescriber(), TimeProvider.System);

        var sql = dashboard.BuildRecentActivityEntityFactQuery(
                ReadScope(),
                new Dictionary<string, long[]>(StringComparer.Ordinal)
                {
                    [entityType] = [42L],
                })
            .ToQueryString();

        sql.Should().Contain($"\"{expectedTable}\"");
        sql.Should().Contain("= ANY",
            "the entity key restriction must remain in the generated branch");
        sql.Should().Contain("public.rc_api_effective_capability_scopes");
        sql.Should().Contain("reports.read");
        sql.Should().NotContain("AtomicAuditLogs");
        sql.Should().NotContain("UNION ALL",
            "a page with one entity type must not generate branches for absent types");
    }

    private static LeaseManagementQueryService NewLeaseManagementQueryService(RentalCommandDbContext db) =>
        new(db, TimeProvider.System);

    private static LeaseManagementReadContext ReadAccess() =>
        new(17, 5, Guid.Parse("77777777-7777-7777-7777-777777777777"), 12, 3);

    private static RentalCommand.Core.Authorization.WorkspaceReadScope ReadScope() =>
        new(17, 5, Guid.Parse("77777777-7777-7777-7777-777777777777"), 12, 3);

    private static void AssertCanonicalPropertyAuthorization(
        string sql,
        int? expectedCallCount = 1,
        bool expectMembershipRoleAssignments = false)
    {
        var calls = Regex.Matches(
            sql,
            """
            FROM\s+public\.rc_api_effective_capability_scopes\(\s*
                (?<portfolio>@[A-Za-z0-9_]+),\s*
                (?<session>@[A-Za-z0-9_]+),\s*
                (?<user>@[A-Za-z0-9_]+),\s*
                (?<context>@[A-Za-z0-9_]+),\s*
                (?<revision>@[A-Za-z0-9_]+),\s*
                (?<capabilities>@[A-Za-z0-9_]+),\s*
                (?<targetKind>@[A-Za-z0-9_]+)\)
            """,
            RegexOptions.IgnorePatternWhitespace);

        calls.Count.Should().BeGreaterThan(0,
            "authorization must use the canonical PostgreSQL effective-capability scope");
        if (expectedCallCount is not null)
        {
            calls.Count.Should().Be(
                expectedCallCount.Value,
                "this reader should evaluate its capability set through the expected canonical scope calls");
        }

        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            AssertParameterReferenceValue(sql, call.Groups["portfolio"].Value, 17);
            AssertParameterReferenceValue(
                sql,
                call.Groups["session"].Value,
                Guid.Parse("77777777-7777-7777-7777-777777777777"));
            AssertParameterReferenceValue(sql, call.Groups["user"].Value, 5);
            AssertParameterReferenceValue(sql, call.Groups["context"].Value, 12);
            AssertParameterReferenceValue(sql, call.Groups["revision"].Value, 3L);
            AssertParameterReferenceValue(sql, call.Groups["targetKind"].Value, "Property");
        }
        sql.Should().Contain("\"ScopeKind\"");
        sql.Should().Contain("\"PropertyId\"");
        sql.Should().Contain("'AllProperties'");
        sql.Should().Contain("'SelectedProperties'");
        sql.Should().NotContain("AuthSessions");
        sql.Should().NotContain("RoleProfileCapabilities");
        if (expectMembershipRoleAssignments)
        {
            sql.Should().Contain("MembershipRoleAssignments");
        }
        else
        {
            sql.Should().NotContain("MembershipRoleAssignments");
        }
        sql.Should().NotContain("MembershipRoleAssignmentProperties");
        sql.Should().NotContain("ClientEvaluation");
        SqlWithoutParameterDeclarations(sql).TrimStart().Should()
            .StartWith("SELECT")
            .And.NotContain(";",
                "the complete authorization and property filter must translate to one PostgreSQL statement");
    }

    private static void AssertParameterReferenceValue(
        string sql,
        string parameterReference,
        object value)
    {
        var expectedValue = Regex.Escape(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        sql.Should().MatchRegex(
            $"-- {Regex.Escape(parameterReference.TrimStart('@'))}='?{expectedValue}'?",
            $"{parameterReference} must bind the canonical authorization input");
    }

    private static string SqlWithoutParameterDeclarations(string sql) => string.Join(
        '\n',
        sql.Split('\n').Where(line => !line.StartsWith("-- ", StringComparison.Ordinal)));

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}

using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Leasing;

/// <summary>
/// Reads one exact legal draft through a property-authorized SQL statement. Presentation values,
/// canonical terms, source identity, and draft-state guards come from the same database snapshot.
/// </summary>
public sealed class LegalDocumentIssuanceDraftReader : ILegalDocumentIssuanceDraftReader
{
    private readonly RentalCommandDbContext _db;

    public LegalDocumentIssuanceDraftReader(RentalCommandDbContext db) => _db = db;

    public Task<LegalDocumentIssuanceDraftSnapshot?> ReadAgreementAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        int expectedDraftRevision,
        DateTime securityNowUtc,
        CancellationToken ct = default) =>
        BuildAgreementQuery(
                scope,
                leaseManagementId,
                leaseAgreementId,
                expectedDraftRevision,
                securityNowUtc)
            .SingleOrDefaultAsync(ct);

    internal IQueryable<LegalDocumentIssuanceDraftSnapshot> BuildAgreementQuery(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        int expectedDraftRevision,
        DateTime securityNowUtc)
    {
        var authorizedProperties = AuthorizedProperties(scope, securityNowUtc);
        return (
            from property in authorizedProperties
            join management in _db.LeaseManagements.AsNoTracking()
                on new { PropertyId = property.Id, property.PortfolioId }
                equals new { management.PropertyId, management.PortfolioId }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { LeaseManagementId = management.Id, management.PortfolioId }
                equals new { agreement.LeaseManagementId, agreement.PortfolioId }
            where management.Id == leaseManagementId && agreement.Id == leaseAgreementId
            select new LegalDocumentIssuanceDraftSnapshot(
                nameof(LeaseAgreement),
                agreement.PortfolioId,
                agreement.LeaseManagementId,
                agreement.Id,
                agreement.DraftRevision,
                agreement.DraftRevision == expectedDraftRevision,
                agreement.IssuedAtUtc == null && agreement.IssuedArtifactId == null
                    && agreement.FullyExecutedAtUtc == null && agreement.ExecutedArtifactId == null
                    && agreement.VoidedAtUtc == null && agreement.DraftCanceledAtUtc == null,
                agreement.DocumentSourceVersionId,
                agreement.TermsSchemaVersion,
                agreement.TermsPayload,
                property.Id,
                agreement.AgreementNumber,
                agreement.TermStartOn,
                agreement.TermEndOn,
                agreement.BaseRentAmount,
                agreement.SecurityDepositObligation,
                agreement.LateFeeAmount,
                agreement.RentDueDay,
                property.Portfolio!.ManagementCompanyName ?? property.Portfolio.Name,
                agreement.Signers
                    .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                    .OrderBy(signer => signer.SigningOrder)
                    .ThenBy(signer => signer.Id)
                    .Select(signer => signer.NameSnapshot)
                    .FirstOrDefault() ?? string.Empty,
                agreement.Signers
                    .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                    .OrderBy(signer => signer.SigningOrder)
                    .ThenBy(signer => signer.Id)
                    .Select(signer => signer.EmailSnapshot)
                    .FirstOrDefault() ?? string.Empty,
                property.Name,
                property.AddressLine1,
                property.AddressLine2,
                property.City,
                property.State,
                property.PostalCode,
                management.Unit!.UnitNumber,
                property.YearBuilt));
    }

    public Task<LegalDocumentIssuanceDraftSnapshot?> ReadAddendumAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAddendumId,
        int expectedDraftRevision,
        DateTime securityNowUtc,
        CancellationToken ct = default) =>
        BuildAddendumQuery(
                scope,
                leaseManagementId,
                leaseAddendumId,
                expectedDraftRevision,
                securityNowUtc)
            .SingleOrDefaultAsync(ct);

    internal IQueryable<LegalDocumentIssuanceDraftSnapshot> BuildAddendumQuery(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAddendumId,
        int expectedDraftRevision,
        DateTime securityNowUtc)
    {
        var authorizedProperties = AuthorizedProperties(scope, securityNowUtc);
        return (
            from property in authorizedProperties
            join management in _db.LeaseManagements.AsNoTracking()
                on new { PropertyId = property.Id, property.PortfolioId }
                equals new { management.PropertyId, management.PortfolioId }
            join addendum in _db.LeaseAddenda.AsNoTracking()
                on new { LeaseManagementId = management.Id, management.PortfolioId }
                equals new { addendum.LeaseManagementId, addendum.PortfolioId }
            join baseAgreement in _db.LeaseAgreements.AsNoTracking()
                on new { AgreementId = addendum.BaseAgreementId, addendum.PortfolioId }
                equals new { AgreementId = baseAgreement.Id, baseAgreement.PortfolioId }
            where management.Id == leaseManagementId && addendum.Id == leaseAddendumId
            select new LegalDocumentIssuanceDraftSnapshot(
                nameof(LeaseAddendum),
                addendum.PortfolioId,
                addendum.LeaseManagementId,
                addendum.Id,
                addendum.DraftRevision,
                addendum.DraftRevision == expectedDraftRevision,
                addendum.IssuedAtUtc == null && addendum.IssuedArtifactId == null
                    && addendum.FullyExecutedAtUtc == null && addendum.ExecutedArtifactId == null
                    && addendum.VoidedAtUtc == null && addendum.DraftCanceledAtUtc == null,
                addendum.DocumentSourceVersionId,
                addendum.TermsSchemaVersion,
                addendum.TermsPayload,
                property.Id,
                addendum.AddendumNumber,
                addendum.EffectiveFromOn,
                addendum.EffectiveThroughOn,
                baseAgreement.BaseRentAmount,
                baseAgreement.SecurityDepositObligation,
                baseAgreement.LateFeeAmount,
                baseAgreement.RentDueDay,
                property.Portfolio!.ManagementCompanyName ?? property.Portfolio.Name,
                addendum.Signers
                    .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                    .OrderBy(signer => signer.SigningOrder)
                    .ThenBy(signer => signer.Id)
                    .Select(signer => signer.NameSnapshot)
                    .FirstOrDefault() ?? string.Empty,
                addendum.Signers
                    .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                    .OrderBy(signer => signer.SigningOrder)
                    .ThenBy(signer => signer.Id)
                    .Select(signer => signer.EmailSnapshot)
                    .FirstOrDefault() ?? string.Empty,
                property.Name,
                property.AddressLine1,
                property.AddressLine2,
                property.City,
                property.State,
                property.PostalCode,
                management.Unit!.UnitNumber,
                property.YearBuilt)
            {
                FinancialEffects = addendum.FinancialEffects
                    .OrderBy(effect => effect.EffectType)
                    .ThenBy(effect => effect.EffectiveFromOn)
                    .ThenBy(effect => effect.EffectiveThroughOn)
                    .ThenBy(effect => effect.DueOn)
                    .ThenBy(effect => effect.ChargeCode)
                    .ThenBy(effect => effect.Currency)
                    .ThenBy(effect => effect.Amount)
                    .ThenBy(effect => effect.Description)
                    .ThenBy(effect => effect.Id)
                    .Select(effect => new LegalDocumentIssuanceFinancialEffect(
                        effect.Id,
                        effect.EffectType,
                        effect.Amount,
                        effect.Currency,
                        effect.ChargeCode,
                        effect.EffectiveFromOn,
                        effect.EffectiveThroughOn,
                        effect.DueOn,
                        effect.Description))
                    .ToList(),
            });
    }

    private IQueryable<Property> AuthorizedProperties(WorkspaceReadScope scope, DateTime securityNowUtc)
    {
        var properties = _db.Properties.AsNoTracking();
        return properties
            .WhereAuthorized(_db, scope, CapabilityKeys.RentalsManage, securityNowUtc)
            .Union(properties.WhereAuthorized(
                _db, scope, CapabilityKeys.LeasingAgreementsPrepare, securityNowUtc));
    }
}

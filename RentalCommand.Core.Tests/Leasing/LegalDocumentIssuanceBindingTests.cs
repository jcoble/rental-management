using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Tests.Leasing;

public sealed class LegalDocumentIssuanceBindingTests
{
    private const string ArtifactSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Correct_source_terms_and_artifact_match_the_canonical_fingerprint()
    {
        var rendered = Fingerprint(sourceVersionId: 17, terms: "{\"rent\":1450,\"tenant\":\"Ada\"}");
        var issued = Fingerprint(sourceVersionId: 17, terms: "{ \"tenant\": \"Ada\", \"rent\": 1450 }");

        LegalDocumentIssuanceBinding.Matches(rendered, issued).Should().BeTrue(
            "equivalent object terms are canonicalized before the legal artifact is bound");
    }

    [Fact]
    public void Different_document_source_version_is_rejected()
    {
        var rendered = Fingerprint(sourceVersionId: 17);
        var changedSource = Fingerprint(sourceVersionId: 18);

        LegalDocumentIssuanceBinding.Matches(rendered, changedSource).Should().BeFalse();
    }

    [Fact]
    public void Different_artifact_content_hash_is_rejected()
    {
        var rendered = Fingerprint(sourceVersionId: 17);
        var changedArtifact = Fingerprint(
            sourceVersionId: 17,
            artifactSha256: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        LegalDocumentIssuanceBinding.Matches(rendered, changedArtifact).Should().BeFalse();
    }

    [Fact]
    public void Different_terms_are_rejected()
    {
        var rendered = Fingerprint(sourceVersionId: 17, terms: "{\"rent\":1450}");
        var changedTerms = Fingerprint(sourceVersionId: 17, terms: "{\"rent\":1500}");

        LegalDocumentIssuanceBinding.Matches(rendered, changedTerms).Should().BeFalse();
    }

    [Fact]
    public void Caller_supplied_noncanonical_fingerprint_is_rejected()
    {
        var expected = Fingerprint(sourceVersionId: 17);

        LegalDocumentIssuanceBinding.Matches(new string('f', 64), expected).Should().BeFalse();
    }

    [Fact]
    public void Changed_typed_addendum_financial_effect_is_rejected()
    {
        var preparedEffects = new[]
        {
            new LegalDocumentIssuanceFinancialEffect(
                LeaseAddendumFinancialEffectId: 71,
                EffectType: LeaseAddendumFinancialEffectType.RecurringRentDelta,
                Amount: 100m,
                Currency: "USD",
                ChargeCode: "PET_RENT",
                EffectiveFromOn: new DateOnly(2026, 8, 1),
                EffectiveThroughOn: null,
                DueOn: null,
                Description: "Monthly pet rent"),
        };
        var changedEffects = preparedEffects
            .Select(effect => effect with { Amount = 125m })
            .ToArray();

        var prepared = AddendumFingerprint(preparedEffects);
        var changedAtIssue = AddendumFingerprint(changedEffects);

        LegalDocumentIssuanceBinding.Matches(prepared, changedAtIssue).Should().BeFalse(
            "the locked issue-time effect snapshot must match the typed effects rendered during preparation");
    }

    private static string Fingerprint(
        int sourceVersionId,
        string terms = "{\"rent\":1450}",
        string artifactSha256 = ArtifactSha256) =>
        LegalDocumentIssuanceBinding.Create(
            legalKind: "LeaseAgreement",
            portfolioId: 3,
            leaseManagementId: 11,
            legalDocumentId: 29,
            draftRevision: 4,
            documentSourceVersionId: sourceVersionId,
            termsSchemaVersion: 1,
            termsPayload: terms,
            artifactContentSha256: artifactSha256,
            artifactByteLength: 4096,
            fileName: "agreement-29.pdf");

    private static string AddendumFingerprint(
        IReadOnlyList<LegalDocumentIssuanceFinancialEffect> financialEffects) =>
        LegalDocumentIssuanceBinding.CreateAddendum(
            portfolioId: 3,
            leaseManagementId: 11,
            leaseAddendumId: 31,
            draftRevision: 4,
            documentSourceVersionId: 17,
            termsSchemaVersion: 1,
            termsPayload: "{\"kind\":\"pet\"}",
            financialEffects: financialEffects,
            artifactContentSha256: ArtifactSha256,
            artifactByteLength: 4096,
            fileName: "addendum-31.pdf");
}

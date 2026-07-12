using FluentAssertions;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Esign;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Source-level contract for the destructive Agreement e-sign cutover. PostgreSQL behavior is
/// intentionally concentrated in the claim statements so the worker cannot accidentally fall
/// back to a legacy Lease envelope or application-clock eligibility decision.
/// </summary>
public sealed class CanonicalLeaseEsignContractTests
{
    [Fact]
    public void SignatureRequest_has_exactly_the_canonical_legal_artifact_parents()
    {
        var properties = typeof(SignatureRequest).GetProperties().Select(property => property.Name).ToArray();

        properties.Should().Contain([nameof(SignatureRequest.LeaseAgreementId), nameof(SignatureRequest.LeaseAddendumId)]);
        properties.Should().NotContain("LeaseId");
        properties.Should().NotContain("OriginalDocumentId");
        properties.Should().NotContain("SignedDocumentId");
    }

    [Fact]
    public void SignatureSigner_persists_only_a_token_digest()
    {
        var properties = typeof(SignatureSigner).GetProperties().Select(property => property.Name).ToArray();

        properties.Should().Contain(nameof(SignatureSigner.TokenHash));
        properties.Should().NotContain("Token");
    }

    [Fact]
    public void Execution_claims_are_one_postgres_statement_against_agreements_and_database_time()
    {
        NativeEsignExecutionClaimStore.BatchClaimSql.Should().Contain("INNER JOIN \"LeaseAgreements\"");
        NativeEsignExecutionClaimStore.BatchClaimSql.Should().Contain("clock_timestamp()");
        NativeEsignExecutionClaimStore.BatchClaimSql.Should().Contain("FOR UPDATE OF request SKIP LOCKED");
        NativeEsignExecutionClaimStore.BatchClaimSql.Should().Contain("pg_try_advisory_xact_lock");
        NativeEsignExecutionClaimStore.BatchClaimSql.Should().NotContain("\"Leases\"");

        NativeEsignExecutionClaimStore.SingleClaimSql.Should().Contain("INNER JOIN \"LeaseAgreements\"");
        NativeEsignExecutionClaimStore.SingleClaimSql.Should().Contain("clock_timestamp()");
        NativeEsignExecutionClaimStore.SingleClaimSql.Should().NotContain("\"Leases\"");
    }
}

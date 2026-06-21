using FluentAssertions;
using RentalCommand.Api.Services.Domain;
using Xunit;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Unit guard for the QuickBooks doc-number capping (ported from EdiPlatform's
/// <c>QuickBooksErpProviderTests</c>). QuickBooks rejects a DocNumber over 21 chars, so an
/// RC external doc number is capped and the overflow folded into a stable hash suffix — the
/// SAME input must always map to the SAME doc number (idempotency on push depends on it).
/// </summary>
public class QuickBooksAccountingProviderTests
{
    [Fact]
    public void ToQuickBooksDocNumber_WithinLimit_PreservesValue()
    {
        var docNumber = QuickBooksAccountingProvider.ToQuickBooksDocNumber("RCP-20260618-01");

        docNumber.Should().Be("RCP-20260618-01");
    }

    [Fact]
    public void ToQuickBooksDocNumber_ExceedsLimit_ReturnsStableCappedValue()
    {
        const string docNumber = "RCP-PORTFOLIO-7-LEASE-12345-PAYMENT-67890";

        var first = QuickBooksAccountingProvider.ToQuickBooksDocNumber(docNumber);
        var second = QuickBooksAccountingProvider.ToQuickBooksDocNumber(docNumber);

        // 21-char cap = a 12-char prefix + '-' + an 8-char hash of the full value.
        first.Length.Should().BeLessThanOrEqualTo(21);
        first.Should().StartWith("RCP-PORTFOLI");
        first.Should().NotBe(docNumber);
        // Deterministic: the same input always caps to the same value (idempotency-safe).
        second.Should().Be(first);
    }
}

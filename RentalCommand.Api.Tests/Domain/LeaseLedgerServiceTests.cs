using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Contract-level proofs for the canonical ledger response. SQL translation and paging are pinned in
/// <see cref="CanonicalLeaseReaderSqlTests"/> against the Npgsql provider.
/// </summary>
public sealed class LeaseLedgerServiceTests
{
    [Fact]
    public void Ledger_response_names_canonical_relationship_and_account_ids_only()
    {
        typeof(LeaseLedgerResponse).GetProperty(nameof(LeaseLedgerResponse.LeaseManagementId))
            .Should().NotBeNull();
        typeof(LeaseLedgerResponse).GetProperty(nameof(LeaseLedgerResponse.TenantAccountId))
            .Should().NotBeNull();
        typeof(LeaseLedgerResponse).GetProperty(nameof(LeaseLedgerResponse.AccountNumber))
            .Should().NotBeNull();
        typeof(LeaseLedgerResponse).GetProperty("LeaseId").Should().BeNull();
        typeof(LeaseLedgerResponse).GetProperty("LeaseNumber").Should().BeNull();
        typeof(LedgerTransactionResponse).GetProperty(nameof(LedgerTransactionResponse.Id))!
            .PropertyType.Should().Be(typeof(long));
    }

    [Fact]
    public void Posted_receipt_and_charge_keep_plain_English_ledger_meaning()
    {
        LedgerExplanation.ForTenantLedgerEntry(
                TenantLedgerEntryType.PaymentReceipt,
                TenantLedgerDirection.Credit,
                1_200m,
                new DateOnly(2026, 3, 3),
                null,
                "Check",
                "March rent")
            .Should().Be("Payment of $1,200 received by check on Mar 3.");

        LedgerExplanation.ForTenantLedgerEntry(
                TenantLedgerEntryType.RentCharge,
                TenantLedgerDirection.Debit,
                1_200m,
                new DateOnly(2026, 3, 1),
                new DateOnly(2026, 4, 1),
                null,
                "April rent")
            .Should().Be("Rent for April 2026 — $1,200 due Apr 1.");
    }
}

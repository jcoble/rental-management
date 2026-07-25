using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
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
    public void Ledger_is_exposed_only_on_the_canonical_lease_management_route()
    {
        typeof(LeaseManagementController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/v1/lease-managements");
        typeof(LeaseManagementController).GetMethod(nameof(LeaseManagementController.Ledger))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("{leaseManagementId:int}/ledger");

        typeof(LeaseManagementController).Assembly
            .GetType("RentalCommand.Api.Controllers.LeaseController")
            .Should().BeNull("the mutable legacy /api/v1/leases surface is deleted, not wrapped");
    }

    [Fact]
    public void Canonical_legal_history_artifacts_and_questions_have_precise_routes()
    {
        typeof(LeaseManagementController).GetMethod(nameof(LeaseManagementController.Ask))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template
            .Should().Be("{leaseManagementId:int}/ask");
        typeof(LeaseAgreementController).GetMethod(nameof(LeaseAgreementController.ListPage))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("page");
        typeof(LeaseAgreementController).GetMethod(nameof(LeaseAgreementController.DownloadArtifact))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("{leaseAgreementId:int}/artifacts/{artifactId:int}");
        typeof(LeaseAgreementController).GetMethod(nameof(LeaseAgreementController.DownloadSourceScan))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("{leaseAgreementId:int}/source-scan");
        typeof(LeaseAddendumController).GetMethod(nameof(LeaseAddendumController.ListPage))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("page");
        typeof(LeaseAddendumController).GetMethod(nameof(LeaseAddendumController.DownloadArtifact))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("{leaseAddendumId:int}/artifacts/{artifactId:int}");
    }

    [Fact]
    public void Voided_issued_agreement_recovery_has_a_dedicated_idempotent_controller_route()
    {
        var method = typeof(LeaseAgreementController).GetMethod(
            nameof(LeaseAgreementController.ReplaceIssuedAgreementWithDraft))!;

        method.GetCustomAttribute<HttpPostAttribute>()!.Template.Should()
            .Be("{sourceAgreementId:int}/issued-replacement-draft");
        method.GetParameters().Single(parameter => parameter.Name == "idempotencyKey")
            .GetCustomAttribute<FromHeaderAttribute>()!.Name.Should().Be("Idempotency-Key");
        typeof(LeaseAgreementController).Should().BeDerivedFrom<ManagementControllerBase>(
            "recovery must retain canonical management authorization and property scope");
    }

    [Fact]
    public void Canonical_relationship_page_and_detail_have_stable_routes()
    {
        typeof(LeaseManagementController).GetMethod(nameof(LeaseManagementController.ListPage))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("page");
        typeof(LeaseManagementController).GetMethod(nameof(LeaseManagementController.Get))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{leaseManagementId:int}");

        typeof(LeaseManagementSummaryResponse).GetProperty("Id").Should().BeNull();
        typeof(LeaseManagementSummaryResponse).GetProperty(nameof(LeaseManagementSummaryResponse.LeaseManagementId))
            .Should().NotBeNull();
        typeof(LeaseManagementSummaryResponse).GetProperty(nameof(LeaseManagementSummaryResponse.LeaseAgreementId))
            .Should().NotBeNull();
        typeof(LeaseManagementSummaryResponse).GetProperty(nameof(LeaseManagementSummaryResponse.TenantAccountId))
            .Should().NotBeNull();
    }

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

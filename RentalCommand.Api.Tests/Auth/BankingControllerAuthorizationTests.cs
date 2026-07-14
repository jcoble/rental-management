using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public class BankingControllerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(BankingController.Summary), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.Connections), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.PlaidSettings), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.PlaidLinkToken), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.ExchangePlaidPublicToken), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.SyncConnection), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.Transactions), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.Import), CapabilityKeys.BankConnectionsManage)]
    [InlineData(nameof(BankingController.Match), CapabilityKeys.MoneyReconciliationOperate)]
    [InlineData(nameof(BankingController.ReviewQueue), CapabilityKeys.MoneyReconciliationOperate)]
    [InlineData(nameof(BankingController.ConfirmMatch), CapabilityKeys.MoneyReconciliationOperate)]
    [InlineData(nameof(BankingController.Route), CapabilityKeys.MoneyReconciliationOperate)]
    [InlineData(nameof(BankingController.ClearMatch), CapabilityKeys.MoneyReconciliationDestructive)]
    [InlineData(nameof(BankingController.DismissMatch), CapabilityKeys.MoneyReconciliationDestructive)]
    [InlineData(nameof(BankingController.Ignore), CapabilityKeys.MoneyReconciliationDestructive)]
    public void BankingActions_RequireTheirExactCanonicalCapability(string actionName, string capabilityKey)
    {
        var method = typeof(BankingController).GetMethods().Single(candidate => candidate.Name == actionName);
        var declared = method
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        declared.Roles.Should().BeNullOrWhiteSpace();
        declared.Policy.Should().Be(CapabilityPolicy.Prefix + capabilityKey);
    }

    [Fact]
    public void BankingController_DoesNotUseOneWorkspacePolicyForEveryAction()
    {
        typeof(BankingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void OperationalDtos_DoNotSerializeBankAdministrationOrTargetIdentifiers()
    {
        var transactionProperties = typeof(OperationalBankTransactionResponse)
            .GetProperties().Select(property => property.Name);
        transactionProperties.Should().NotContain(property => new[]
        {
            "BankConnectionId",
            "InstitutionName",
            "AccountName",
            "ProviderTransactionId",
            "MatchedTenantAccountId",
            "MatchedTenantLedgerEntryId",
            "MatchedExpenseId",
            "Notes",
        }.Contains(property));

        var suggestionProperties = typeof(OperationalBankMatchSuggestionResponse)
            .GetProperties().Select(property => property.Name);
        suggestionProperties.Should().NotContain(property => new[]
        {
            "EntityType",
            "EntityId",
            "TenantAccountId",
        }.Contains(property));
    }
}

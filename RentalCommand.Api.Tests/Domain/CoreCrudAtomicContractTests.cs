using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Tests.Domain;

public sealed class CoreCrudAtomicContractTests
{
    [Theory]
    [InlineData(typeof(OwnerEntityController))]
    [InlineData(typeof(TenantController))]
    [InlineData(typeof(VendorController))]
    public void EveryCoreCrudMutationRequiresAnIdempotencyKey(Type controllerType)
    {
        var mutations = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name is "Create" or "Update" or "Delete")
            .ToArray();

        mutations.Should().HaveCount(3);
        mutations.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() != null &&
            parameter.GetCustomAttribute<FromHeaderAttribute>()!.Name == "Idempotency-Key"));
    }

    [Fact]
    public void CoreCrudHandlerReauthorizesReplaysAndHasNoDirectSetBasedEscapeHatch()
    {
        typeof(AtomicCoreCrudMutationHandler)
            .Should().Implement<IAtomicCommandHandler<AtomicCoreCrudMutationCommand, AtomicCoreCrudMutationResult>>();

        var source = ReadSource("RentalCommand.Api", "Services", "Domain", "AtomicCoreCrudMutation.cs");
        source.Should().Contain("BindSemanticAudit");
        source.Should().Contain("StageDataUpdate");
        source.Should().Contain("ReadDatabaseClockUtcAsync");
        source.Should().NotContain("ExecuteUpdateAsync");
        source.Should().NotContain("ExecuteDeleteAsync");
    }

    [Fact]
    public void UnitDeleteEndpointLetsAtomicReplayAuthorizeTombstonedUnitReceipts()
    {
        var controller = ReadSource("RentalCommand.Api", "Controllers", "UnitController.cs");
        var deleteStart = controller.IndexOf("public async Task<IActionResult> Delete(", StringComparison.Ordinal);
        var deleteEnd = controller.IndexOf("catch (UnauthorizedAccessException ex)", deleteStart, StringComparison.Ordinal);
        var deleteEndpoint = controller[deleteStart..deleteEnd];

        deleteEndpoint.Should().Contain("_service.DeleteAsync(scope, id, operationKey, ct)");
        deleteEndpoint.Should().NotContain("UnitCapabilityAuthorizationTarget",
            "a successful delete receipt must be replayable after the Unit row is soft-deleted");
    }

    [Fact]
    public void PropertySetupIsOneReceiptBackedMutationWithNoImplicitCanonicalUnitPath()
    {
        var setup = typeof(PropertyController).GetMethod(nameof(PropertyController.Setup));
        setup.Should().NotBeNull();
        setup!.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key")
            .Should().BeTrue();
        typeof(PropertyController).GetMethod("Create").Should().BeNull(
            "Property creation must go through setup so Property and Units commit together");

        var source = ReadSource("RentalCommand.Api", "Services", "Domain", "AtomicCoreCrudMutation.cs");
        source.Should().Contain("AtomicCoreCrudMutationOperation.Setup");
        source.Should().Contain("SetupPropertyAsync");
        source.Should().NotContain("NewCanonicalUnit");
        source.Should().NotContain("Canonical unit created");
    }

    [Fact]
    public void PropertyOwnershipUsesOnlyCanonicalEffectiveDatedRelationshipFacts()
    {
        typeof(Property).GetProperty("OwnerId").Should().BeNull();
        typeof(Property).GetProperty("OwnerEntityId").Should().BeNull();
        typeof(Property).GetProperty("Owner").Should().BeNull();
        typeof(Property).GetProperty("OwnerEntity").Should().BeNull();
        typeof(PropertyResponse).GetProperty("OwnerEntityId").Should().BeNull();
        typeof(PropertyResponse).GetProperty("OwnerName").Should().BeNull();
        typeof(CreatePropertyRequest).GetProperty("OwnerEntityId").Should().BeNull();
        typeof(UpdatePropertyRequest).GetProperty("OwnerEntityId").Should().BeNull();
        typeof(CreatePropertyRequest).GetProperty(nameof(CreatePropertyRequest.Ownerships))
            .Should().NotBeNull();
        typeof(UpdatePropertyRequest).GetProperty(nameof(UpdatePropertyRequest.Ownerships))
            .Should().NotBeNull();

        var source = ReadSource("RentalCommand.Api", "Services", "Domain", "AtomicCoreCrudMutation.cs");
        source.Should().Contain("new PropertyOwnership");
        source.Should().Contain("OwnershipSharePercent = request.OwnershipSharePercent");
        source.Should().Contain("StatementRecipientName =");
        source.Should().Contain("StatementRecipientEmail =");
        source.Should().Contain("PayeeName =");
        source.Should().Contain("ownership.EffectiveToUtc = now");
        source.Should().Contain("ReplaceCurrentOwnershipsAsync");
        source.Should().NotContain("property.OwnerId");
        source.Should().NotContain("property.OwnerEntityId");
    }

    [Fact]
    public void GuidedTenantSetupIsOneReceiptBackedBatchWithoutAClientMutationLoop()
    {
        var endpoint = typeof(TenantController).GetMethod(nameof(TenantController.CreateGuidedSetupBatch));
        endpoint.Should().NotBeNull();
        endpoint!.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key")
            .Should().BeTrue();
        typeof(AtomicGuidedTenantSetupHandler)
            .Should().Implement<IAtomicCommandHandler<AtomicGuidedTenantSetupCommand, AtomicGuidedTenantSetupResult>>();

        var handler = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "AtomicGuidedTenantSetup.cs");
        handler.Should().Contain("CapabilityKeys.SecurityManage");
        handler.Should().NotContain("CapabilityKeys.RentalsManage");
        handler.Should().NotContain("CapabilityKeys.LeasingOnboardingManage");

        var onboarding = ReadSource("web", "src", "routes", "(protected)", "onboarding", "+page.svelte");
        onboarding.Should().Contain("tenants.createGuidedSetupBatch(rows)");
        onboarding.Should().NotContain("made.push(await tenants.create(t))");
        onboarding.Should().NotContain("for (const t of rows)");
    }

    [Fact]
    public void LiveClientsUseTheSharedRetryKeyHelpers()
    {
        foreach (var endpoint in new[] { "properties.ts", "owners.ts", "tenants.ts", "vendors.ts" })
        {
            ReadSource("web", "src", "lib", "api", "endpoints", endpoint)
                .Should().Contain("idempotentMutation");
        }

        foreach (var repository in new[]
        {
            new[] { "properties", "properties_repository.dart" },
            new[] { "owners", "owners_repository.dart" },
            new[] { "tenants", "tenants_repository.dart" },
            new[] { "vendors", "vendors_repository.dart" },
        })
        {
            ReadSource("mobile", "lib", "features", repository[0], repository[1])
                .Should().Contain("IdempotentMutation.run");
        }
    }

    private static string ReadSource(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
    }
}

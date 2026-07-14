using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Api.Tests.Domain;

public sealed class CoreCrudAtomicContractTests
{
    [Theory]
    [InlineData(typeof(PropertyController))]
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
            .Should().Implement<IAtomicReplayAuthorizer<AtomicCoreCrudMutationCommand>>();

        var source = ReadSource("RentalCommand.Api", "Services", "Domain", "AtomicCoreCrudMutation.cs");
        source.Should().Contain("BindSemanticAudit");
        source.Should().Contain("StageDataUpdate");
        source.Should().Contain("ReadDatabaseClockUtcAsync");
        source.Should().NotContain("ExecuteUpdateAsync");
        source.Should().NotContain("ExecuteDeleteAsync");
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

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Sandbox;
using RentalCommand.Api.Services.Auth;

namespace RentalCommand.Api.Tests.Domain;

public sealed class SandboxLifecycleAtomicContractTests
{
    [Theory]
    [InlineData(nameof(SandboxController.GoLive))]
    [InlineData(nameof(SandboxController.OnboardingChoice))]
    public void Sandbox_mutations_require_client_idempotency_key(string methodName)
    {
        var method = typeof(SandboxController).GetMethod(methodName)!;

        method.GetParameters().Should().Contain(parameter =>
            parameter.GetCustomAttributes<FromHeaderAttribute>()
                .Any(attribute => attribute.Name == "Idempotency-Key"));
    }

    [Fact]
    public void Sandbox_lifecycle_is_receipt_backed_typed_atomic_command()
    {
        typeof(SandboxLifecycleCommand).Should().Implement<IAtomicCommandData>();
        typeof(SandboxLifecycleCommandHandler).Should()
            .Implement<IAtomicCommandHandler<SandboxLifecycleCommand, SandboxLifecycleResult>>();
    }

    [Fact]
    public void Rich_demo_seed_and_legal_finalization_are_typed_replay_authorized_commands()
    {
        typeof(SeedDemoPortfolioCommand).Should().Implement<IAtomicCommandData>();
        typeof(DemoSeedCommandHandler).Should()
            .Implement<IAtomicCommandHandler<SeedDemoPortfolioCommand, SeedDemoPortfolioResult>>();

        typeof(FinalizeDemoLegalDocumentCommand).Should().Implement<IAtomicCommandData>();
        typeof(DemoLegalDocumentFinalizeCommandHandler).Should()
            .Implement<IAtomicCommandHandler<FinalizeDemoLegalDocumentCommand, FinalizeDemoLegalDocumentResult>>();
    }

    [Fact]
    public void Sandbox_lifecycle_handler_has_no_nested_atomic_execution_path()
    {
        typeof(SandboxLifecycleCommandHandler)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Should().NotContain(typeof(IAtomicUnitOfWork));
    }
}

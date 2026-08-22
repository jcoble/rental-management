using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Time;
using RentalCommand.Data.Simulation;

namespace RentalCommand.Api.Tests.Simulation;

public sealed class SimulationAtomicContractTests
{
    [Fact]
    public void EveryDevClockMutationRequiresAnIdempotencyKey()
    {
        var mutations = typeof(DevClockController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name is
                nameof(DevClockController.Set) or
                nameof(DevClockController.Advance) or
                nameof(DevClockController.Freeze) or
                nameof(DevClockController.Unfreeze) or
                nameof(DevClockController.Reset))
            .ToArray();

        mutations.Should().HaveCount(5);
        mutations.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() != null
            && parameter.GetCustomAttribute<FromHeaderAttribute>()!.Name == "Idempotency-Key"));
    }

    [Fact]
    public void EveryDevWorkerEnqueueMutationRequiresAnIdempotencyKey()
    {
        var mutations = typeof(DevWorkersController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name is nameof(DevWorkersController.RunOnce) or nameof(DevWorkersController.RunDue))
            .ToArray();

        mutations.Should().HaveCount(2);
        mutations.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() != null
            && parameter.GetCustomAttribute<FromHeaderAttribute>()!.Name == "Idempotency-Key"));
    }

    [Fact]
    public void SimulationCommandsIgnoreOnlyAuthEnvelopeFieldsFromInterfaceAnnotations()
    {
        var baseline = new SetSimulationClockCommand(
            7,
            11,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            13,
            17,
            new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc),
            ClockMode.Frozen,
            true,
            "America/New_York");
        var changedEnvelope = baseline with
        {
            AuthSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AccessContextId = 19,
            ExpectedAccessRevision = 23,
        };
        var changedActor = baseline with { ActorUserId = 12 };

        AtomicCommandFingerprint.Create(changedEnvelope).Should()
            .Be(AtomicCommandFingerprint.Create(baseline));
        AtomicCommandFingerprint.Create(changedActor).Should()
            .NotBe(AtomicCommandFingerprint.Create(baseline));
    }

    [Fact]
    public void SimulationHandlersUseReceiptReplayAuthorizationAndDatabaseClockedWorkspaceCapabilityChecks()
    {
        var handlers = new[]
        {
            typeof(SetSimulationClockRule),
            typeof(AdvanceSimulationClockRule),
            typeof(FreezeSimulationClockRule),
            typeof(UnfreezeSimulationClockRule),
            typeof(ResetSimulationClockRule),
            typeof(EnqueueSimulationWorkerCommandRule),
        };
        handlers.All(handler =>
            handler.GetMethod("ExecuteAsync") is not null
            && handler.GetMethod("AuthorizeReplayAsync") is not null)
            .Should().BeTrue();

        var source = ReadSource("RentalCommand.Data", "Simulation", "SimulationAtomicCommandHandlers.cs");
        source.Should().Contain("ReadDatabaseClockUtcAsync");
        source.Should().Contain("FOR UPDATE");
        source.Should().Contain("StageSemanticEvent");
        source.Should().Contain("CapabilityAuthorizationTargetKind.Workspace");
        source.Should().Contain("MembershipRoleAssignmentScopeKind.AllProperties");
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

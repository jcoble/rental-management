using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class MoneyMutationIdempotencyContractTests : IDisposable
{
    private readonly SqliteTestContext _context = new();

    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData(typeof(ExpenseController))]
    [InlineData(typeof(RecurringExpenseController))]
    [InlineData(typeof(LoanController))]
    [InlineData(typeof(OwnerDistributionController))]
    public void EveryMoneyCrudMutationRequiresTheIdempotencyHeader(Type controllerType)
    {
        var mutationMethods = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name is "Create" or "Update" or "Delete")
            .ToArray();

        mutationMethods.Should().HaveCount(3);
        var headerRequirements = mutationMethods.Select(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() is { Name: "Idempotency-Key" }));
        headerRequirements.Should().OnlyContain(required => required);
    }

    [Fact]
    public async Task ExpenseMutationsRejectMissingBlankAndOversizedKeysBeforeCallingTheService()
    {
        var service = new Mock<IExpenseService>(MockBehavior.Strict);
        var controller = new ExpenseController(
            service.Object,
            Mock.Of<ICapitalAssetService>(),
            _context.Db,
            Mock.Of<IFileStorage>());

        var create = await controller.Create(
            request: new CreateExpenseRequest(),
            idempotencyKey: null,
            ct: CancellationToken.None);
        var update = await controller.Update(
            id: 1,
            request: new UpdateExpenseRequest(),
            idempotencyKey: "   ",
            ct: CancellationToken.None);
        var delete = await controller.Delete(
            id: 1,
            idempotencyKey: new string('x', 129),
            ct: CancellationToken.None);

        create.Result.Should().BeOfType<BadRequestObjectResult>();
        update.Result.Should().BeOfType<BadRequestObjectResult>();
        delete.Should().BeOfType<BadRequestObjectResult>();
        service.VerifyNoOtherCalls();
    }
}

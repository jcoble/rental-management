using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests.Domain;

public sealed class TenantDtoValidationTests
{
    [Fact]
    public void CreateAndUpdateTenantRequestsAcceptTheSharedInternationalPhoneLengthContract()
    {
        var create = new CreateTenantRequest
        {
            FirstName = "Tenant",
            LastName = "Example",
            Phone = new string('١', 50),
        };
        var update = new UpdateTenantRequest { Phone = new string('١', 50) };

        Validate(create).Should().BeEmpty();
        Validate(update).Should().BeEmpty();
    }

    [Fact]
    public void CreateAndUpdateTenantRequestsRejectPhonesBeyondTheRenderedMaxlength()
    {
        var create = new CreateTenantRequest
        {
            FirstName = "Tenant",
            LastName = "Example",
            Phone = new string('١', 51),
        };
        var update = new UpdateTenantRequest { Phone = new string('١', 51) };

        Validate(create).Should().Contain(result => result.MemberNames.Contains(nameof(CreateTenantRequest.Phone)));
        Validate(update).Should().Contain(result => result.MemberNames.Contains(nameof(UpdateTenantRequest.Phone)));
    }

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}

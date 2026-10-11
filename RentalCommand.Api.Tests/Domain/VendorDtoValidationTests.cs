using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests.Domain;

public sealed class VendorDtoValidationTests
{
    [Fact]
    public void CreateAndUpdateVendorRequestsAcceptTheSharedInternationalLengthContract()
    {
        var create = new CreateVendorRequest
        {
            Name = "Vendor",
            ServiceType = "Plumbing",
            PostalCode = new string('P', 20),
            Phone = new string('1', 50),
        };
        var update = new UpdateVendorRequest
        {
            PostalCode = new string('P', 20),
            Phone = new string('1', 50),
        };

        Validate(create).Should().BeEmpty();
        Validate(update).Should().BeEmpty();
    }

    [Fact]
    public void CreateAndUpdateVendorRequestsRejectValuesBeyondTheRenderedMaxlengths()
    {
        var createPostal = new CreateVendorRequest
        {
            Name = "Vendor",
            ServiceType = "Plumbing",
            PostalCode = new string('P', 21),
        };
        var createPhone = new CreateVendorRequest
        {
            Name = "Vendor",
            ServiceType = "Plumbing",
            Phone = new string('1', 51),
        };
        var updatePostal = new UpdateVendorRequest { PostalCode = new string('P', 21) };
        var updatePhone = new UpdateVendorRequest { Phone = new string('1', 51) };

        Validate(createPostal).Should().Contain(result => result.MemberNames.Contains(nameof(CreateVendorRequest.PostalCode)));
        Validate(createPhone).Should().Contain(result => result.MemberNames.Contains(nameof(CreateVendorRequest.Phone)));
        Validate(updatePostal).Should().Contain(result => result.MemberNames.Contains(nameof(UpdateVendorRequest.PostalCode)));
        Validate(updatePhone).Should().Contain(result => result.MemberNames.Contains(nameof(UpdateVendorRequest.Phone)));
    }

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}

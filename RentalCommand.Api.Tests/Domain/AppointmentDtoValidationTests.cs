using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AppointmentDtoValidationTests
{
    [Fact]
    public void S26_POT_1_CreateAppointmentRejectsAnEndBeforeTheStartAtTheModelBoundary()
    {
        var request = new CreateAppointmentRequest
        {
            Title = "Showing",
            ScheduledStart = new DateTime(2027, 1, 1, 9, 0, 0),
            ScheduledEnd = new DateTime(2027, 1, 1, 8, 59, 0),
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(result =>
            result.MemberNames.Contains(nameof(CreateAppointmentRequest.ScheduledEnd)) &&
            result.ErrorMessage == "The end time must be after the start time");
    }

    [Fact]
    public void S26_POT_1_CreateAppointmentRejectsAnEndEqualToTheStart()
    {
        var request = new CreateAppointmentRequest
        {
            Title = "Showing",
            ScheduledStart = new DateTime(2027, 1, 1, 9, 0, 0),
            ScheduledEnd = new DateTime(2027, 1, 1, 9, 0, 0),
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle(result =>
            result.MemberNames.Contains(nameof(CreateAppointmentRequest.ScheduledEnd)) &&
            result.ErrorMessage == "The end time must be after the start time");
    }

    [Fact]
    public void S26_POT_1_UpdateAppointmentRejectsAnEndEqualToTheStart()
    {
        var request = new UpdateAppointmentRequest
        {
            ScheduledStart = new DateTime(2027, 1, 1, 9, 0, 0),
            ScheduledEnd = new DateTime(2027, 1, 1, 9, 0, 0),
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle(result =>
            result.MemberNames.Contains(nameof(UpdateAppointmentRequest.ScheduledEnd)) &&
            result.ErrorMessage == "The end time must be after the start time");
    }
}

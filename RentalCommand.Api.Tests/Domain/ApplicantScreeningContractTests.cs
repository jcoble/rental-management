using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Screening;
using RentalCommand.Data;
using RentalCommand.Data.Screening;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Domain;

public sealed class ApplicantScreeningContractTests
{
    [Fact]
    public void Disabled_adapter_is_honest_and_collects_no_sensitive_data()
    {
        var provider = new DisabledScreeningProvider();

        provider.Descriptor.IsConfigured.Should().BeFalse();
        provider.Descriptor.Capabilities.CreatesHostedInvitation.Should().BeFalse();
        var fields = typeof(ScreeningInvitationRequest).GetProperties().Select(p => p.Name).ToArray();
        fields.Should().NotContain("Ssn");
        fields.Should().NotContain("DateOfBirth");
        fields.Should().NotContain("IdentityAnswers");
        fields.Should().NotContain("RawReport");
    }

    [Fact]
    public void External_tracking_accepts_any_provider_without_provider_specific_fields()
    {
        var request = new TrackExternalScreeningRequest
        {
            OperationKey = "screening-attempt-1",
            ProviderDisplayName = "Landlord-selected service",
            ProviderReference = "external-42",
            Status = ApplicantScreeningStatus.InProgress,
        };

        request.ProviderDisplayName.Should().Be("Landlord-selected service");
        var fields = typeof(TrackExternalScreeningRequest).GetProperties().Select(p => p.Name).ToArray();
        fields.Should().NotContain("ZillowAccountId");
        fields.Should().NotContain("VendorSpecificOrderId");
    }

    [Fact]
    public void Persisted_response_contains_workflow_metadata_but_not_report_content()
    {
        var fields = typeof(ApplicantScreeningResponse).GetProperties().Select(p => p.Name).ToArray();
        fields.Should().Contain("ProviderReference");
        fields.Should().Contain("ProviderHostedUrl");
        fields.Should().Contain("Status");
        fields.Should().Contain("LastStatusAtUtc");
        fields.Should().NotContain("RawResultJson");
        fields.Should().NotContain("CreditScore");
        fields.Should().NotContain("CriminalRecords");
        fields.Should().NotContain("EvictionRecords");
        fields.Should().NotContain("Ssn");
    }

    [Fact]
    public void Provider_invitation_carries_the_callers_stable_recovery_key()
    {
        var request = new ScreeningInvitationRequest(
            42, "stable-attempt-key", "Applicant", "applicant@example.test",
            new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc));

        request.OperationKey.Should().Be("stable-attempt-key");
        (request with { ApplicantName = "Corrected display name" }).OperationKey
            .Should().Be(request.OperationKey);
    }

    [Fact]
    public void Application_history_filter_and_sort_translate_to_one_postgres_statement()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=screening_query_contract;Username=test;Password=test")
            .Options;
        using var db = new RentalCommandDbContext(options);

        var sql = db.ApplicantScreenings
            .ForApplication(portfolioId: 7, applicationId: 42)
            .Select(screening => new
            {
                screening.Id,
                screening.Mode,
                screening.Status,
                screening.ProviderDisplayName,
                screening.LastStatusAtUtc,
            })
            .ToQueryString();

        sql.Should().Contain("WHERE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("\"PortfolioId\"");
        sql.Should().Contain("\"ApplicationId\"");
    }
}

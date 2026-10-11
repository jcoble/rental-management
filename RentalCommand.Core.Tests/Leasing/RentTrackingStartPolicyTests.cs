using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Tests.Leasing;

public sealed class RentTrackingStartPolicyTests
{
    private static readonly DateOnly LeaseStart = new(2026, 1, 1);
    private static readonly DateOnly BusinessDate = new(2026, 7, 15);

    [Fact]
    public void Backfill_uses_null_as_the_explicit_lease_start_policy()
    {
        RentTrackingStartPolicy.Resolve(
                LeaseStart,
                RentTrackingStartMode.BackfillFromLeaseStart,
                null,
                BusinessDate)
            .Should().BeNull();
    }

    [Fact]
    public void Forward_only_starts_at_the_later_of_the_lease_and_business_dates()
    {
        RentTrackingStartPolicy.Resolve(
                LeaseStart,
                RentTrackingStartMode.ForwardOnly,
                null,
                BusinessDate)
            .Should().Be(BusinessDate);

        RentTrackingStartPolicy.Resolve(
                new DateOnly(2026, 9, 1),
                RentTrackingStartMode.ForwardOnly,
                null,
                BusinessDate)
            .Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Custom_start_is_clamped_to_the_lease_start()
    {
        RentTrackingStartPolicy.Resolve(
                LeaseStart,
                RentTrackingStartMode.CustomCutoffDate,
                new DateOnly(2025, 12, 1),
                BusinessDate)
            .Should().Be(LeaseStart);

        RentTrackingStartPolicy.Resolve(
                LeaseStart,
                RentTrackingStartMode.CustomCutoffDate,
                new DateOnly(2026, 4, 20),
                BusinessDate)
            .Should().Be(new DateOnly(2026, 4, 20));
    }

    [Fact]
    public void Custom_start_requires_a_date()
    {
        var act = () => RentTrackingStartPolicy.Resolve(
            LeaseStart,
            RentTrackingStartMode.CustomCutoffDate,
            null,
            BusinessDate);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Rent tracking cutoff date is required.");
    }
}

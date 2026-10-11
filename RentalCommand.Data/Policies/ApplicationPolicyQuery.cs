using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Policies;

namespace RentalCommand.Data.Policies;

public static class ApplicationPolicyQuery
{
    public static IQueryable<RentalApplication> OpenForEmail(
        this IQueryable<RentalApplication> applications,
        int portfolioId,
        string normalizedEmail) =>
        applications.AsNoTracking().Where(application =>
            application.PortfolioId == portfolioId
            && application.DeletedAt == null
            && application.Email != null
            && application.Email.Trim().ToLower() == normalizedEmail
            && (application.Status == ApplicationStatus.Submitted
                || application.Status == ApplicationStatus.UnderReview
                || application.Status == ApplicationStatus.Approved));
}

public static class PropertyUnitResolutionQuery
{
    public static IQueryable<PropertyUnitResolution> ResolvePropertyUnit(
        this RentalCommandDbContext db,
        int portfolioId,
        int? requestedPropertyId,
        int? requestedUnitId) =>
        db.Set<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId && portfolio.DeletedAt == null)
            .Select(portfolio => new PropertyUnitResolution
            {
                RequestedPropertyId = requestedPropertyId,
                RequestedUnitId = requestedUnitId,
                PropertyId = requestedPropertyId > 0
                    ? db.Set<Property>()
                        .Where(property => property.Id == requestedPropertyId
                            && property.PortfolioId == portfolio.Id && property.DeletedAt == null)
                        .Select(property => (int?)property.Id).SingleOrDefault()
                    : null,
                UnitId = requestedUnitId > 0
                    ? db.Set<Unit>()
                        .Where(unit => unit.Id == requestedUnitId
                            && unit.PortfolioId == portfolio.Id && unit.DeletedAt == null)
                        .Select(unit => (int?)unit.Id).SingleOrDefault()
                    : null,
                UnitPropertyId = requestedUnitId > 0
                    ? db.Set<Unit>()
                        .Where(unit => unit.Id == requestedUnitId
                            && unit.PortfolioId == portfolio.Id && unit.DeletedAt == null)
                        .Select(unit => (int?)unit.PropertyId).SingleOrDefault()
                    : null,
            });

    public static IQueryable<UnitReferenceResolution> ResolveUnit(
        this RentalCommandDbContext db,
        int portfolioId,
        int unitId) =>
        db.Set<Unit>().AsNoTracking()
            .Where(unit => unit.Id == unitId
                && unit.PortfolioId == portfolioId
                && unit.DeletedAt == null)
            .Select(unit => new UnitReferenceResolution
            {
                UnitId = unit.Id,
                PropertyId = unit.PropertyId,
            });
}

public sealed class PropertyUnitResolution
{
    public int? RequestedPropertyId { get; init; }
    public int? RequestedUnitId { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public int? UnitPropertyId { get; init; }

    public bool PropertyWasRequestedButNotFound => RequestedPropertyId is > 0 && PropertyId is null;
    public bool UnitWasRequestedButNotFound => RequestedUnitId is > 0 && UnitId is null;
    public bool UnitDoesNotBelongToProperty =>
        PropertyUnitPolicy.DoesNotBelong(PropertyId, UnitPropertyId);
    public int? ResolvedPropertyId => UnitPropertyId ?? PropertyId;
}

public sealed class UnitReferenceResolution
{
    public int UnitId { get; init; }
    public int PropertyId { get; init; }

    public bool DoesNotBelongTo(int? selectedPropertyId) =>
        PropertyUnitPolicy.DoesNotBelong(selectedPropertyId, PropertyId);
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class UnitListingService : IUnitListingService
{
    private const string EntityType = "UnitListing";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;

    public UnitListingService(RentalCommandDbContext db, IDataUpdateService dataUpdate, IAuditTrailService audit)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
    }

    public Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default)
        => _db.Units.AsNoTracking()
            .AnyAsync(u => u.Id == unitId && u.Property != null && u.Property.PortfolioId == portfolioId, ct);

    public async Task<UnitListingResponse?> GetForUnitAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        var listing = await _db.UnitListings
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && l.UnitId == unitId && l.Channel == UnitListingChannel.ZillowManual)
            .OrderByDescending(l => l.UpdatedAt)
            .ThenByDescending(l => l.Id)
            .FirstOrDefaultAsync(ct);

        return listing is null ? null : UnitListingResponse.FromEntity(listing);
    }

    public async Task<UnitListingResponse?> GenerateForUnitAsync(
        int portfolioId,
        int unitId,
        int userId,
        CancellationToken ct = default)
    {
        var seed = await LoadSeedAsync(portfolioId, unitId, ct);
        if (seed is null)
            return null;

        var now = DateTime.UtcNow;
        var listing = await LoadTrackedListingAsync(portfolioId, unitId, ct);
        var isNew = listing is null;
        listing ??= new UnitListing
        {
            PortfolioId = portfolioId,
            PropertyId = seed.PropertyId,
            UnitId = seed.UnitId,
            Channel = UnitListingChannel.ZillowManual,
            Status = UnitListingStatus.Draft,
            CreatedAt = now,
        };

        ApplyGeneratedFacts(listing, seed);
        listing.UpdatedAt = now;

        if (isNew)
            _db.UnitListings.Add(listing);

        await _db.SaveChangesAsync(ct);

        var response = UnitListingResponse.FromEntity(listing);
        await _audit.LogAsync(
            portfolioId,
            EntityType,
            listing.Id,
            isNew ? AuditLogOperation.Created : AuditLogOperation.Updated,
            userId: userId,
            changeReason: "Generated Zillow manual listing packet",
            ct: ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, listing.Id, response, ct);
        return response;
    }

    public async Task<UnitListingResponse?> SaveAsync(
        int portfolioId,
        int unitId,
        SaveUnitListingRequest request,
        int userId,
        CancellationToken ct = default)
    {
        var seed = await LoadSeedAsync(portfolioId, unitId, ct);
        if (seed is null)
            return null;

        var now = DateTime.UtcNow;
        var listing = await LoadTrackedListingAsync(portfolioId, unitId, ct);
        var isNew = listing is null;
        listing ??= new UnitListing
        {
            PortfolioId = portfolioId,
            PropertyId = seed.PropertyId,
            UnitId = seed.UnitId,
            Channel = UnitListingChannel.ZillowManual,
            Status = UnitListingStatus.Draft,
            CreatedAt = now,
        };

        if (isNew)
            ApplyGeneratedFacts(listing, seed);

        ApplyRequest(listing, request, now);
        listing.UpdatedAt = now;

        if (isNew)
            _db.UnitListings.Add(listing);

        await _db.SaveChangesAsync(ct);

        var response = UnitListingResponse.FromEntity(listing);
        await _audit.LogAsync(
            portfolioId,
            EntityType,
            listing.Id,
            isNew ? AuditLogOperation.Created : AuditLogOperation.Updated,
            userId: userId,
            changeReason: "Saved Zillow manual listing packet",
            ct: ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, listing.Id, response, ct);
        return response;
    }

    private Task<UnitListing?> LoadTrackedListingAsync(int portfolioId, int unitId, CancellationToken ct)
        => _db.UnitListings
            .FirstOrDefaultAsync(l =>
                l.PortfolioId == portfolioId
                && l.UnitId == unitId
                && l.Channel == UnitListingChannel.ZillowManual, ct);

    private Task<UnitListingSeed?> LoadSeedAsync(int portfolioId, int unitId, CancellationToken ct)
        => _db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId && u.Property != null && u.Property.PortfolioId == portfolioId)
            .Select(u => new UnitListingSeed(
                u.PropertyId,
                u.Id,
                u.Property!.Name,
                u.Property!.AddressLine1,
                u.Property!.AddressLine2,
                u.Property!.City,
                u.Property!.State,
                u.Property!.PostalCode,
                u.UnitNumber,
                u.Bedrooms,
                u.Bathrooms,
                u.SquareFeet,
                u.MarketRent))
            .FirstOrDefaultAsync(ct);

    private static void ApplyGeneratedFacts(UnitListing listing, UnitListingSeed seed)
    {
        listing.PropertyId = seed.PropertyId;
        listing.Rent = seed.MarketRent;
        listing.SecurityDeposit ??= seed.MarketRent > 0 ? seed.MarketRent : null;
        listing.Bedrooms = seed.Bedrooms;
        listing.Bathrooms = seed.Bathrooms;
        listing.SquareFeet = seed.SquareFeet;
        listing.Headline = BuildHeadline(seed);
        listing.Description = BuildDescription(seed);
        listing.LeaseTerms ??= "12-month lease";
        listing.PetPolicy ??= string.Empty;
        listing.Utilities ??= string.Empty;
        listing.Parking ??= string.Empty;
        listing.Amenities ??= string.Empty;
        listing.PhotoNotes ??= "Add exterior, kitchen, bathroom, bedroom, living area, and utility photos.";
    }

    private static void ApplyRequest(UnitListing listing, SaveUnitListingRequest request, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(request.Status))
            listing.Status = ParseStatus(request.Status);

        if (request.Headline is not null)
            listing.Headline = CleanRequired(request.Headline, "Listing headline");
        if (request.Description is not null)
            listing.Description = CleanRequired(request.Description, "Listing description");
        if (request.Rent is not null)
            listing.Rent = request.Rent.Value;
        if (request.SecurityDeposit is not null)
            listing.SecurityDeposit = request.SecurityDeposit;
        if (request.Bedrooms is not null)
            listing.Bedrooms = request.Bedrooms.Value;
        if (request.Bathrooms is not null)
            listing.Bathrooms = request.Bathrooms.Value;
        if (request.SquareFeet is not null)
            listing.SquareFeet = request.SquareFeet;
        if (request.AvailableOn is not null)
            listing.AvailableOn = request.AvailableOn.Value.ToUniversalTime();

        listing.LeaseTerms = CleanOptional(request.LeaseTerms, listing.LeaseTerms);
        listing.PetPolicy = CleanOptional(request.PetPolicy, listing.PetPolicy);
        listing.Utilities = CleanOptional(request.Utilities, listing.Utilities);
        listing.Parking = CleanOptional(request.Parking, listing.Parking);
        listing.Amenities = CleanOptional(request.Amenities, listing.Amenities);
        listing.PhotoNotes = CleanOptional(request.PhotoNotes, listing.PhotoNotes);
        listing.ZillowListingUrl = CleanOptional(request.ZillowListingUrl, listing.ZillowListingUrl);
        listing.ZillowApplicationUrl = CleanOptional(request.ZillowApplicationUrl, listing.ZillowApplicationUrl);

        if (request.PostedAtUtc is not null)
            listing.PostedAtUtc = request.PostedAtUtc.Value.ToUniversalTime();
        else if (listing.Status == UnitListingStatus.Posted && listing.PostedAtUtc is null)
            listing.PostedAtUtc = now;

        ValidateRequiredCopy(listing);
    }

    private static UnitListingStatus ParseStatus(string value)
        => Enum.TryParse<UnitListingStatus>(value, ignoreCase: true, out var status)
            ? status
            : throw new DomainValidationException("Listing status is invalid.");

    private static string? CleanOptional(string? incoming, string? existing)
        => incoming is null ? existing : string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();

    private static string CleanRequired(string incoming, string fieldName)
    {
        var value = incoming.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{fieldName} is required.")
            : value;
    }

    private static void ValidateRequiredCopy(UnitListing listing)
    {
        if (string.IsNullOrWhiteSpace(listing.Headline))
            throw new DomainValidationException("Listing headline is required.");
        if (string.IsNullOrWhiteSpace(listing.Description))
            throw new DomainValidationException("Listing description is required.");
    }

    private static string BuildHeadline(UnitListingSeed seed)
        => $"{FormatRooms(seed.Bedrooms, "bed")} / {FormatRooms(seed.Bathrooms, "bath")} at {seed.PropertyName} - Unit {seed.UnitNumber}";

    private static string BuildDescription(UnitListingSeed seed)
    {
        var parts = new List<string>
        {
            $"Now available at {seed.PropertyName}, Unit {seed.UnitNumber}.",
            $"This rental offers {FormatRooms(seed.Bedrooms, "bedroom")}, {FormatRooms(seed.Bathrooms, "bathroom")}, and listed rent of {Money(seed.MarketRent)} per month.",
        };

        if (seed.SquareFeet is > 0)
            parts.Add($"Approximate size: {seed.SquareFeet.Value:N0} sq ft.");

        var address = ComposeAddress(seed);
        if (!string.IsNullOrWhiteSpace(address))
            parts.Add($"Property address: {address}.");

        parts.Add("Review all details in Zillow Rental Manager before posting.");
        return string.Join(" ", parts);
    }

    private static string ComposeAddress(UnitListingSeed seed)
    {
        var street = string.Join(" ", new[] { seed.AddressLine1, seed.AddressLine2 }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var cityStateZip = string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(seed.City) ? null : seed.City + ",",
            seed.State,
            seed.PostalCode,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.Join(" ", new[] { street, cityStateZip }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatRooms(decimal value, string singular)
    {
        var formatted = value % 1m == 0m
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{formatted} {singular}";
    }

    private static string Money(decimal value)
        => value.ToString("C0", CultureInfo.GetCultureInfo("en-US"));

    private sealed record UnitListingSeed(
        int PropertyId,
        int UnitId,
        string PropertyName,
        string AddressLine1,
        string? AddressLine2,
        string City,
        string State,
        string PostalCode,
        string UnitNumber,
        decimal Bedrooms,
        decimal Bathrooms,
        int? SquareFeet,
        decimal MarketRent);
}

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Import;

/// <inheritdoc cref="ICsvImportService"/>
public sealed class CsvImportService : ICsvImportService
{
    private readonly RentalCommandDbContext _db;
    private readonly ITenantService _tenants;
    private readonly IPropertyService _properties;
    private readonly IUnitService _units;

    // Defined column sets per entity type (the order doubles as the downloadable template header).
    private static readonly string[] TenantColumns = ["firstName", "lastName", "email", "phone"];
    private static readonly string[] PropertyColumns = ["name", "addressLine1", "addressLine2", "city", "state", "postalCode", "type"];
    private static readonly string[] UnitColumns = ["propertyName", "propertyId", "unitNumber", "bedrooms", "bathrooms", "marketRent"];

    public CsvImportService(
        RentalCommandDbContext db,
        ITenantService tenants,
        IPropertyService properties,
        IUnitService units)
    {
        _db = db;
        _tenants = tenants;
        _properties = properties;
        _units = units;
    }

    public string GetTemplate(string entityType) =>
        string.Join(",", ColumnsFor(entityType));

    public async Task<CsvImportResult> ImportAsync(
        int portfolioId, string entityType, Stream csv, bool dryRun, CancellationToken ct = default)
    {
        var canonicalType = Canonicalize(entityType); // throws ArgumentException for unsupported types

        string text;
        using (var reader = new StreamReader(csv, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(ct);
        }

        var table = CsvParser.Parse(text); // throws CsvFormatException when there is no header row

        // Map header names → column index, case-insensitively. Duplicate headers keep the first.
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Header.Length; i++)
        {
            columnIndex.TryAdd(table.Header[i], i);
        }

        var rowResults = new List<CsvImportRowResult>(table.Rows.Count);
        var validCount = 0;
        var createdCount = 0;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            // Row number as the user sees it: header is row 1, so the first data row is row 2.
            var rowNumber = r + 2;
            var cells = table.Rows[r];

            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var idx) && idx < cells.Length
                    ? cells[idx].Trim()
                    : null;

            var errors = new List<string>();

            // Build + validate the create request, then (when committing) create it. A failure of any
            // single row is captured as that row's errors and never aborts the whole import.
            int? createdId = null;
            var valid = false;
            try
            {
                switch (canonicalType)
                {
                    case "Tenant":
                        valid = await TryImportTenantAsync(portfolioId, Cell, dryRun, errors, id => createdId = id, ct);
                        break;
                    case "Property":
                        valid = await TryImportPropertyAsync(portfolioId, Cell, dryRun, errors, id => createdId = id, ct);
                        break;
                    case "Unit":
                        valid = await TryImportUnitAsync(portfolioId, Cell, dryRun, errors, id => createdId = id, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Defensive: a row should never throw, but if it does, record it instead of failing the run.
                errors.Add($"Unexpected error: {ex.Message}");
                valid = false;
            }

            if (valid)
            {
                validCount++;
                if (createdId.HasValue)
                {
                    createdCount++;
                }
            }

            rowResults.Add(new CsvImportRowResult
            {
                RowNumber = rowNumber,
                Valid = valid,
                Errors = errors,
                CreatedId = createdId,
            });
        }

        return new CsvImportResult
        {
            EntityType = canonicalType,
            DryRun = dryRun,
            TotalRows = table.Rows.Count,
            ValidRows = validCount,
            CreatedRows = createdCount,
            Rows = rowResults,
        };
    }

    // -------------------------------------------------------------------------
    // Per-entity row import
    // -------------------------------------------------------------------------

    private async Task<bool> TryImportTenantAsync(
        int portfolioId, Func<string, string?> cell, bool dryRun, List<string> errors, Action<int> setId, CancellationToken ct)
    {
        var request = new CreateTenantRequest
        {
            FirstName = cell("firstName") ?? string.Empty,
            LastName = cell("lastName") ?? string.Empty,
            Email = NullIfEmpty(cell("email")),
            Phone = NullIfEmpty(cell("phone")),
        };

        if (!TryValidate(request, errors))
        {
            return false;
        }

        if (!dryRun)
        {
            var created = await _tenants.CreateAsync(portfolioId, request, ct);
            setId(created.Id);
        }

        return true;
    }

    private async Task<bool> TryImportPropertyAsync(
        int portfolioId, Func<string, string?> cell, bool dryRun, List<string> errors, Action<int> setId, CancellationToken ct)
    {
        var request = new CreatePropertyRequest
        {
            Name = cell("name") ?? string.Empty,
            AddressLine1 = cell("addressLine1") ?? string.Empty,
            AddressLine2 = NullIfEmpty(cell("addressLine2")),
            City = cell("city") ?? string.Empty,
            State = cell("state") ?? string.Empty,
            PostalCode = cell("postalCode") ?? string.Empty,
        };

        // type defaults sensibly (MultiFamily); only override when a parseable value is supplied.
        var typeRaw = NullIfEmpty(cell("type"));
        if (typeRaw != null)
        {
            if (Enum.TryParse<PropertyType>(typeRaw, ignoreCase: true, out var type))
            {
                request.PropertyType = type;
            }
            else
            {
                errors.Add($"type '{typeRaw}' is not a valid property type. Allowed: {string.Join(", ", Enum.GetNames<PropertyType>())}.");
            }
        }

        if (!TryValidate(request, errors))
        {
            return false;
        }

        if (!dryRun)
        {
            var created = await _properties.CreateAsync(portfolioId, request, ct);
            if (created == null)
            {
                // CreateAsync only returns null here on an owner/owner-entity scope failure, which a
                // CSV import never supplies — treat defensively as a failed row.
                errors.Add("The property could not be created.");
                return false;
            }
            setId(created.Id);
        }

        return true;
    }

    private async Task<bool> TryImportUnitAsync(
        int portfolioId, Func<string, string?> cell, bool dryRun, List<string> errors, Action<int> setId, CancellationToken ct)
    {
        // Resolve the property reference: an explicit propertyId wins; otherwise resolve by name
        // (case-insensitive, in-portfolio). Ambiguous or missing names are a clear per-row error.
        var propertyIdRaw = NullIfEmpty(cell("propertyId"));
        var propertyName = NullIfEmpty(cell("propertyName"));
        int? propertyId = null;

        if (propertyIdRaw != null)
        {
            if (!int.TryParse(propertyIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                errors.Add($"propertyId '{propertyIdRaw}' is not a valid number.");
            }
            else
            {
                var inScope = await _db.Properties
                    .AnyAsync(p => p.Id == parsedId && p.PortfolioId == portfolioId, ct);
                if (!inScope)
                {
                    errors.Add($"propertyId {parsedId} was not found in this portfolio.");
                }
                else
                {
                    propertyId = parsedId;
                }
            }
        }
        else if (propertyName != null)
        {
            // Case-insensitive name match that translates on both Postgres (prod) and SQLite (tests).
            // Take(2) is enough to detect ambiguity without loading every matching row.
            var lowered = propertyName.ToLower();
            var matches = await _db.Properties
                .Where(p => p.PortfolioId == portfolioId && p.Name.ToLower() == lowered)
                .Select(p => p.Id)
                .Take(2)
                .ToListAsync(ct);

            if (matches.Count == 0)
            {
                errors.Add($"No property named '{propertyName}' was found in this portfolio.");
            }
            else if (matches.Count > 1)
            {
                errors.Add($"Property name '{propertyName}' is ambiguous — more than one property matches. Use propertyId instead.");
            }
            else
            {
                propertyId = matches[0];
            }
        }
        else
        {
            errors.Add("A propertyName or propertyId is required.");
        }

        var request = new CreateUnitRequest
        {
            // Use a placeholder when resolution failed so the [Range] annotation doesn't add a
            // confusing second "PropertyId" error on top of our clear property-resolution message;
            // creation below is gated on propertyId.HasValue regardless.
            PropertyId = propertyId ?? 1,
            UnitNumber = cell("unitNumber") ?? string.Empty,
            Bedrooms = ParseDecimal("bedrooms", cell("bedrooms"), errors) ?? 0m,
            Bathrooms = ParseDecimal("bathrooms", cell("bathrooms"), errors) ?? 0m,
            MarketRent = ParseDecimal("marketRent", cell("marketRent"), errors) ?? 0m,
        };

        // Validate the remaining DataAnnotations (UnitNumber required, ranges on bedrooms/bath/rent).
        TryValidate(request, errors);

        if (errors.Count > 0 || !propertyId.HasValue)
        {
            return false;
        }

        if (!dryRun)
        {
            var created = await _units.CreateAsync(portfolioId, request, ct);
            if (created == null)
            {
                errors.Add("The unit could not be created (the property is missing or outside this portfolio).");
                return false;
            }
            setId(created.Id);
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs DataAnnotations validation on a create request (the same attributes the controllers use),
    /// appending any messages to <paramref name="errors"/>. Returns false when the request is invalid.
    /// </summary>
    private static bool TryValidate(object request, List<string> errors)
    {
        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(request, context, results, validateAllProperties: true);
        if (!ok)
        {
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    errors.Add(result.ErrorMessage);
                }
            }
        }
        return ok && errors.Count == 0;
    }

    private static decimal? ParseDecimal(string column, string? raw, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Tolerate a leading currency symbol / thousands separators so a spreadsheet's "$1,200" works.
        var cleaned = raw.Trim().TrimStart('$').Replace(",", string.Empty);
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add($"{column} '{raw}' is not a valid number.");
        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] ColumnsFor(string entityType) => Canonicalize(entityType) switch
    {
        "Tenant" => TenantColumns,
        "Property" => PropertyColumns,
        "Unit" => UnitColumns,
        _ => throw new ArgumentException($"Unsupported import entity type '{entityType}'.", nameof(entityType)),
    };

    /// <summary>Normalizes a client-supplied entity type to its canonical name, or throws.</summary>
    private static string Canonicalize(string entityType)
    {
        foreach (var supported in ICsvImportService.SupportedEntityTypes)
        {
            if (string.Equals(entityType, supported, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        throw new ArgumentException(
            $"Unsupported import entity type '{entityType}'. Supported: {string.Join(", ", ICsvImportService.SupportedEntityTypes)}.",
            nameof(entityType));
    }
}

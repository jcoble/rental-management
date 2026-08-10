using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Import;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Import;

namespace RentalCommand.Api.Services.Import;

/// <inheritdoc cref="ICsvImportService"/>
public sealed class CsvImportService : ICsvImportService
{
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IUnitCsvImportPreviewQuery _unitPreview;
    private readonly ICoreCsvImportPreviewQuery _corePreview;
    private readonly IPaymentCsvImportPreviewQuery _paymentPreview;

    // Defined column sets per entity type (the order doubles as the downloadable template header).
    private static readonly string[] TenantColumns = ["firstName", "lastName", "email", "phone"];
    private static readonly string[] PropertyColumns = ["name", "addressLine1", "addressLine2", "city", "state", "postalCode", "type", "rentalStructure", "unitNumber"];
    private static readonly string[] UnitColumns = ["propertyName", "propertyId", "unitNumber", "bedrooms", "bathrooms", "marketRent"];
    private static readonly string[] PaymentColumns = ["relationshipNumber", "propertyName", "unitNumber", "paymentType", "amount", "paidDate", "method", "externalReference", "notes"];
    private static readonly string[] ExpenseColumns = ["propertyName", "category", "description", "amount", "incurredAt", "paidAt", "notes"];
    private static readonly string[] LoanColumns = ["propertyName", "lender", "originalAmount", "currentBalance", "annualInterestRatePct", "termMonths", "startDate", "dayOfMonthDue", "monthlyPrincipalInterest", "monthlyEscrow"];

    public CsvImportService(
        IAtomicUnitOfWork atomic,
        IUnitCsvImportPreviewQuery unitPreview,
        ICoreCsvImportPreviewQuery corePreview,
        IPaymentCsvImportPreviewQuery paymentPreview)
    {
        _atomic = atomic;
        _unitPreview = unitPreview;
        _corePreview = corePreview;
        _paymentPreview = paymentPreview;
    }

    public string GetTemplate(string entityType) =>
        string.Join(",", ColumnsFor(entityType));

    public async Task<CsvImportResult> ImportAsync(
        WorkspaceReadScope scope,
        string entityType,
        Stream csv,
        bool dryRun,
        CsvImportCommandContext? commandContext = null,
        CancellationToken ct = default)
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

        var rows = table.Rows
            .Select((cells, index) => new ImportRow(index + 2, cells))
            .ToList();
        if (!dryRun && commandContext is null)
            throw new ArgumentException("Authenticated operation context is required for atomic imports.");
        if (canonicalType is "Property" or "Tenant" or "Expense" or "Loan")
        {
            return await ImportCoreAtomicAsync(
                scope, canonicalType, rows, columnIndex, commandContext, dryRun, ct);
        }
        if (canonicalType == "Unit")
        {
            var unitContext = commandContext ?? new CsvImportCommandContext(
                scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision, "dry-run");
            return await ImportUnitsAtomicAsync(
                scope, rows, columnIndex, unitContext, dryRun, ct);
        }
        if (canonicalType == "Payment")
        {
            return await ImportPaymentsAtomicAsync(
                scope, rows, columnIndex, commandContext, dryRun, ct);
        }

        throw new ArgumentOutOfRangeException(nameof(canonicalType));
    }

    // -------------------------------------------------------------------------
    // Per-entity row import
    // -------------------------------------------------------------------------

    private async Task<CsvImportResult> ImportCoreAtomicAsync(
        WorkspaceReadScope scope,
        string canonicalType,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        CsvImportCommandContext? context,
        bool dryRun,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return new CsvImportResult
            {
                EntityType = canonicalType,
                DryRun = dryRun,
                Rows = [],
            };
        }

        var domain = canonicalType switch
        {
            "Property" => AtomicCoreCsvImportDomain.Property,
            "Tenant" => AtomicCoreCsvImportDomain.Tenant,
            "Expense" => AtomicCoreCsvImportDomain.Expense,
            "Loan" => AtomicCoreCsvImportDomain.Loan,
            _ => throw new ArgumentOutOfRangeException(nameof(canonicalType)),
        };
        var rowsJson = domain switch
        {
            AtomicCoreCsvImportDomain.Property => JsonSerializer.Serialize(BuildPropertyRows(rows, columnIndex)),
            AtomicCoreCsvImportDomain.Tenant => JsonSerializer.Serialize(BuildTenantRows(rows, columnIndex)),
            AtomicCoreCsvImportDomain.Expense => JsonSerializer.Serialize(BuildExpenseRows(rows, columnIndex)),
            AtomicCoreCsvImportDomain.Loan => JsonSerializer.Serialize(BuildLoanRows(rows, columnIndex)),
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

        AtomicCoreCsvImportBatchResult batch;
        if (dryRun)
        {
            batch = await _corePreview.PreviewAsync(scope, domain, rowsJson, ct);
        }
        else
        {
            var commandContext = context!.Value;
            var command = new AtomicCoreCsvImportCommand(
                scope.PortfolioId, commandContext.ActorUserId, commandContext.AuthSessionId,
                commandContext.AccessContextId, commandContext.AccessRevision, domain,
                commandContext.OperationKeyDigest, rowsJson);
            var outcome = await _atomic.ExecuteAsync(
                AtomicCoreCsvImport.Identity(command), command, AtomicCoreCsvImport.Codec, ct);
            batch = new AtomicCoreCsvImportBatchResult(
                true, outcome.Value.Rows, [], outcome.Value.TotalRows,
                outcome.Value.ValidRows, outcome.Value.CreatedRows, outcome.Value.DuplicateRows);
        }

        if (!batch.Authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
        return new CsvImportResult
        {
            EntityType = canonicalType,
            DryRun = dryRun,
            TotalRows = batch.TotalRows,
            ValidRows = batch.ValidRows,
            CreatedRows = batch.CreatedCount,
            DuplicateRows = batch.DuplicateRows,
            Rows = Array.ConvertAll(batch.Rows.ToArray(), row => new CsvImportRowResult
            {
                RowNumber = row.RowNumber,
                Valid = row.Valid,
                Errors = row.Errors,
                CreatedId = row.CreatedId,
                IsDuplicate = row.IsDuplicate,
                SkipReason = row.IsDuplicate ? "A matching record already exists." : null,
            }),
        };
    }

    private static AtomicPropertyImportRow[] BuildPropertyRows(
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex) =>
        rows.Select(row =>
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            var request = new CreatePropertyRequest
            {
                Name = Cell("name") ?? string.Empty,
                AddressLine1 = Cell("addressLine1") ?? string.Empty,
                AddressLine2 = NullIfEmpty(Cell("addressLine2")),
                City = Cell("city") ?? string.Empty,
                State = Cell("state") ?? string.Empty,
                PostalCode = Cell("postalCode") ?? string.Empty,
            };
            var typeRaw = NullIfEmpty(Cell("type"));
            if (typeRaw is not null)
            {
                if (Enum.TryParse<PropertyType>(typeRaw, true, out var propertyType))
                    request.PropertyType = propertyType;
                else
                    errors.Add($"type '{typeRaw}' is not a valid property type. Choose one of the listed property types.");
            }
            var structureRaw = NullIfEmpty(Cell("rentalStructure"));
            if (structureRaw is null)
            {
                errors.Add("Rental setup is required.");
            }
            else if (Enum.TryParse<RentalStructure>(structureRaw, true, out var rentalStructure))
            {
                request.RentalStructure = rentalStructure;
            }
            else
            {
                errors.Add($"Rental setup '{structureRaw}' is invalid. Use “SingleRental” for one rental or “MultiRental” for multiple rentals.");
            }
            var unitNumber = NullIfEmpty(Cell("unitNumber"));
            if (unitNumber is null)
                errors.Add("unitNumber is required so the Property and its first explicit Unit are imported together.");
            else if (unitNumber.Length > 50)
                errors.Add("unitNumber cannot exceed 50 characters.");
            TryValidate(request, errors);
            return new AtomicPropertyImportRow(
                row.RowNumber, request.Name, request.AddressLine1, request.AddressLine2,
                request.City, request.State, request.PostalCode, (int)request.PropertyType,
                request.RentalStructure.ToString(), unitNumber ?? string.Empty,
                errors.ToArray());
        }).ToArray();

    private static AtomicTenantImportRow[] BuildTenantRows(
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex) =>
        rows.Select(row =>
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            var request = new CreateTenantRequest
            {
                FirstName = Cell("firstName") ?? string.Empty,
                LastName = Cell("lastName") ?? string.Empty,
                Email = NullIfEmpty(Cell("email")),
                Phone = NullIfEmpty(Cell("phone")),
            };
            TryValidate(request, errors);
            return new AtomicTenantImportRow(
                row.RowNumber, request.FirstName, request.LastName,
                request.Email, request.Phone, errors.ToArray());
        }).ToArray();

    private static AtomicExpenseImportRow[] BuildExpenseRows(
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex) =>
        rows.Select(row =>
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            var amount = ParseRequiredDecimal("amount", Cell("amount"), errors) ?? 0m;
            var incurredAt = ParseRequiredDate("incurredAt", Cell("incurredAt"), errors) ?? default;
            var paidAt = ParseDate("paidAt", Cell("paidAt"), errors);
            var category = ParseEnum("category", Cell("category"), ScheduleECategory.Other, errors)
                ?? ScheduleECategory.Other;
            var request = new CreateExpenseRequest
            {
                PropertyId = 1,
                Category = category,
                Description = Cell("description") ?? string.Empty,
                Status = paidAt.HasValue ? ExpenseStatus.Paid : ExpenseStatus.Pending,
                Amount = amount,
                IncurredAt = incurredAt,
                PaidAt = paidAt,
                Notes = NullIfEmpty(Cell("notes")),
            };
            TryValidate(request, errors);
            if (string.IsNullOrWhiteSpace(Cell("propertyName")))
                errors.Add("propertyName is required.");
            return new AtomicExpenseImportRow(
                row.RowNumber, Cell("propertyName") ?? string.Empty, (int)category,
                request.Description, amount, incurredAt, paidAt, request.Notes, errors.ToArray());
        }).ToArray();

    private static AtomicLoanImportRow[] BuildLoanRows(
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex) =>
        rows.Select(row =>
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            var originalAmount = ParseRequiredDecimal("originalAmount", Cell("originalAmount"), errors) ?? 0m;
            var currentBalance = ParseDecimal("currentBalance", Cell("currentBalance"), errors);
            var rate = ParseDecimal("annualInterestRatePct", Cell("annualInterestRatePct"), errors) ?? 0m;
            var term = ParseRequiredInt("termMonths", Cell("termMonths"), errors) ?? 0;
            var startDate = ParseRequiredDate("startDate", Cell("startDate"), errors) ?? default;
            var dueDay = ParseInt("dayOfMonthDue", Cell("dayOfMonthDue"), errors) ?? 1;
            var principalInterest = ParseDecimal("monthlyPrincipalInterest", Cell("monthlyPrincipalInterest"), errors) ?? 0m;
            var escrow = ParseDecimal("monthlyEscrow", Cell("monthlyEscrow"), errors) ?? 0m;
            var request = new CreateLoanRequest
            {
                PropertyId = 1,
                Lender = Cell("lender") ?? string.Empty,
                OriginalAmount = originalAmount,
                CurrentBalance = currentBalance,
                AnnualInterestRatePct = rate,
                TermMonths = term,
                StartDate = startDate,
                DayOfMonthDue = dueDay,
                MonthlyPrincipalInterest = principalInterest,
                MonthlyEscrow = escrow,
            };
            TryValidate(request, errors);
            if (string.IsNullOrWhiteSpace(Cell("propertyName")))
                errors.Add("propertyName is required.");
            return new AtomicLoanImportRow(
                row.RowNumber, Cell("propertyName") ?? string.Empty, request.Lender,
                originalAmount, currentBalance, rate, term, startDate, dueDay,
                principalInterest, escrow, errors.ToArray());
        }).ToArray();

    private async Task<CsvImportResult> ImportUnitsAtomicAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        CsvImportCommandContext context,
        bool dryRun,
        CancellationToken ct)
    {
        var commands = new List<AtomicUnitImportRow>();
        foreach (var row in rows)
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            int? propertyId = null;
            var propertyIdRaw = NullIfEmpty(Cell("propertyId"));
            if (propertyIdRaw is not null)
            {
                if (int.TryParse(propertyIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0) propertyId = parsed;
                else errors.Add("propertyId must be a positive whole number.");
            }
            var request = new CreateUnitRequest
            {
                PropertyId = propertyId ?? 1,
                UnitNumber = Cell("unitNumber") ?? string.Empty,
                Bedrooms = ParseDecimal("bedrooms", Cell("bedrooms"), errors) ?? 0m,
                Bathrooms = ParseDecimal("bathrooms", Cell("bathrooms"), errors) ?? 0m,
                MarketRent = ParseDecimal("marketRent", Cell("marketRent"), errors) ?? 0m,
            };
            TryValidate(request, errors);
            if (propertyId is null && string.IsNullOrWhiteSpace(Cell("propertyName")))
                errors.Add("propertyName or propertyId is required.");
            commands.Add(new AtomicUnitImportRow(
                row.RowNumber, propertyId, NullIfEmpty(Cell("propertyName")),
                request.UnitNumber.Trim(), request.Bedrooms, request.Bathrooms, request.MarketRent,
                errors.ToArray()));
        }

        if (commands.Count == 0)
        {
            return new CsvImportResult
            {
                EntityType = "Unit",
                DryRun = dryRun,
                Rows = [],
            };
        }

        AtomicUnitImportRowResult[] databaseRows;
        int totalRows;
        int validRows;
        int createdRows;
        int duplicateRows;
        if (dryRun)
        {
            var preview = await _unitPreview.PreviewAsync(scope, commands, ct);
            if (!preview.Authorized)
                throw new UnauthorizedAccessException(
                    "At least one Unit row is outside your assigned property scope.");
            databaseRows = preview.Rows.ToArray();
            totalRows = preview.TotalRows;
            validRows = preview.ValidRows;
            createdRows = preview.CreatedCount;
            duplicateRows = preview.DuplicateRows;
        }
        else
        {
            var command = new AtomicUnitCsvImportCommand(
                scope.PortfolioId, context.ActorUserId, context.AuthSessionId,
                context.AccessContextId, context.AccessRevision,
                context.OperationKeyDigest, commands.ToArray());
            var outcome = await _atomic.ExecuteAsync(
                AtomicUnitCsvImport.Identity(command), command, AtomicUnitCsvImport.Codec, ct);
            databaseRows = outcome.Value.Rows;
            totalRows = outcome.Value.TotalRows;
            validRows = outcome.Value.ValidRows;
            createdRows = outcome.Value.CreatedRows;
            duplicateRows = outcome.Value.DuplicateRows;
        }
        var responseRows = Array.ConvertAll(databaseRows, row => new CsvImportRowResult
        {
            RowNumber = row.RowNumber,
            Valid = row.Valid,
            Errors = row.Errors,
            CreatedId = row.CreatedId,
            IsDuplicate = row.IsDuplicate,
            SkipReason = row.IsDuplicate
                ? "A live unit with this number already exists on the property."
                : null,
        });
        return new CsvImportResult
        {
            EntityType = "Unit",
            DryRun = dryRun,
            TotalRows = totalRows,
            ValidRows = validRows,
            CreatedRows = createdRows,
            DuplicateRows = duplicateRows,
            Rows = responseRows,
        };
    }

    private async Task<CsvImportResult> ImportPaymentsAtomicAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        CsvImportCommandContext? context,
        bool dryRun,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return new CsvImportResult { EntityType = "Payment", DryRun = dryRun, Rows = [] };
        }
        if (rows.Count > 256)
            throw new ArgumentException("Payment CSV imports are limited to 256 rows.");

        var rowsJson = JsonSerializer.Serialize(BuildPaymentRows(
            scope.PortfolioId, rows, columnIndex, context?.OperationKeyDigest ?? "dry-run"));
        AtomicPaymentCsvImportBatchResult batch;
        if (dryRun)
        {
            batch = await _paymentPreview.PreviewAsync(scope, rowsJson, ct);
        }
        else
        {
            var commandContext = context!.Value;
            var command = new AtomicPaymentCsvImportCommand(
                scope.PortfolioId, commandContext.ActorUserId, commandContext.AuthSessionId,
                commandContext.AccessContextId, commandContext.AccessRevision,
                commandContext.OperationKeyDigest, rowsJson);
            var outcome = await _atomic.ExecuteAsync(
                AtomicPaymentCsvImport.Identity(command), command, AtomicPaymentCsvImport.Codec, ct);
            batch = new AtomicPaymentCsvImportBatchResult(
                true, outcome.Value.Rows, [], outcome.Value.TotalRows,
                outcome.Value.ValidRows, outcome.Value.CreatedRows, outcome.Value.DuplicateRows);
        }

        if (!batch.Authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
        return new CsvImportResult
        {
            EntityType = "Payment",
            DryRun = dryRun,
            TotalRows = batch.TotalRows,
            ValidRows = batch.ValidRows,
            CreatedRows = batch.CreatedCount,
            DuplicateRows = batch.DuplicateRows,
            Rows = Array.ConvertAll(batch.Rows.ToArray(), row => new CsvImportRowResult
            {
                RowNumber = row.RowNumber,
                Valid = row.Valid,
                Errors = row.Errors,
                CreatedId = row.CreatedId,
                IsDuplicate = row.IsDuplicate,
                SkipReason = row.IsDuplicate ? "A matching payment already exists." : null,
            }),
        };
    }

    private static AtomicPaymentImportRow[] BuildPaymentRows(
        int portfolioId,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        string operationDigest) =>
        rows.Select(row =>
        {
            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var index) && index < row.Cells.Length
                    ? row.Cells[index].Trim()
                    : null;
            var errors = new List<string>();
            var relationshipNumber = NullIfEmpty(Cell("relationshipNumber"));
            var propertyName = NullIfEmpty(Cell("propertyName"));
            var unitNumber = NullIfEmpty(Cell("unitNumber"));
            if (relationshipNumber is null && propertyName is null)
                errors.Add("relationshipNumber or propertyName is required.");
            var amount = ParseRequiredDecimal("amount", Cell("amount"), errors);
            if (amount is <= 0) errors.Add("amount must be greater than zero.");
            var paidAt = ParseRequiredDate("paidDate", Cell("paidDate"), errors);
            var paymentType = ParseEnum(
                "paymentType", Cell("paymentType"), PaymentType.Rent, errors);
            var notes = NullIfEmpty(Cell("notes"));
            var method = NullIfEmpty(Cell("method")) ?? "Imported payment";
            var externalReference = NullIfEmpty(Cell("externalReference"));
            var description = notes ?? $"Imported {paymentType ?? PaymentType.Rent} payment";
            if (method.Length > 200) errors.Add("method cannot exceed 200 characters.");
            if (externalReference?.Length > 200)
                errors.Add("externalReference cannot exceed 200 characters.");
            if (description.Length > 500) errors.Add("notes cannot exceed 500 characters.");
            return new AtomicPaymentImportRow(
                row.RowNumber,
                relationshipNumber,
                propertyName,
                unitNumber,
                amount ?? 0,
                DateOnly.FromDateTime(paidAt ?? DateTime.UnixEpoch),
                method,
                externalReference,
                description,
                $"csv-receipt:{portfolioId}:{operationDigest}:{row.RowNumber}",
                errors.ToArray());
        }).ToArray();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record ImportRow(int RowNumber, string[] Cells);
    private sealed record AtomicPropertyImportRow(
        int RowNumber,
        string Name,
        string AddressLine1,
        string? AddressLine2,
        string City,
        string State,
        string PostalCode,
        int PropertyType,
        string RentalStructure,
        string UnitNumber,
        string[] Errors);
    private sealed record AtomicTenantImportRow(
        int RowNumber,
        string FirstName,
        string LastName,
        string? Email,
        string? Phone,
        string[] Errors);
    private sealed record AtomicExpenseImportRow(
        int RowNumber,
        string PropertyName,
        int Category,
        string Description,
        decimal Amount,
        DateTime IncurredAt,
        DateTime? PaidAt,
        string? Notes,
        string[] Errors);
    private sealed record AtomicLoanImportRow(
        int RowNumber,
        string PropertyName,
        string Lender,
        decimal OriginalAmount,
        decimal? CurrentBalance,
        decimal AnnualInterestRatePct,
        int TermMonths,
        DateTime StartDate,
        int DayOfMonthDue,
        decimal MonthlyPrincipalInterest,
        decimal MonthlyEscrow,
        string[] Errors);
    private sealed record AtomicPaymentImportRow(
        int RowNumber,
        string? RelationshipNumber,
        string? PropertyName,
        string? UnitNumber,
        decimal Amount,
        DateOnly PaidOn,
        string Method,
        string? ExternalReference,
        string Description,
        string DeliveryKey,
        string[] Errors);

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

    private static decimal? ParseRequiredDecimal(string column, string? raw, List<string> errors)
    {
        var value = ParseDecimal(column, raw, errors);
        if (!value.HasValue && string.IsNullOrWhiteSpace(raw))
        {
            errors.Add($"{column} is required.");
        }
        return value;
    }

    private static int? ParseInt(string column, string? raw, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add($"{column} '{raw}' is not a valid whole number.");
        return null;
    }

    private static int? ParseRequiredInt(string column, string? raw, List<string> errors)
    {
        var value = ParseInt(column, raw, errors);
        if (!value.HasValue && string.IsNullOrWhiteSpace(raw))
        {
            errors.Add($"{column} is required.");
        }
        return value;
    }

    private static DateTime? ParseDate(string column, string? raw, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return value;
        }

        errors.Add($"{column} '{raw}' is not a valid date.");
        return null;
    }

    private static DateTime? ParseRequiredDate(string column, string? raw, List<string> errors)
    {
        var value = ParseDate(column, raw, errors);
        if (!value.HasValue && string.IsNullOrWhiteSpace(raw))
        {
            errors.Add($"{column} is required.");
        }
        return value;
    }

    private static TEnum? ParseEnum<TEnum>(string column, string? raw, TEnum defaultValue, List<string> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var normalized = NormalizeEnumToken(raw);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (NormalizeEnumToken(name) == normalized)
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        errors.Add($"{column} '{raw}' is not valid. Choose one of the listed options.");
        return null;
    }

    private static string NormalizeEnumToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] ColumnsFor(string entityType) => Canonicalize(entityType) switch
    {
        "Tenant" => TenantColumns,
        "Property" => PropertyColumns,
        "Unit" => UnitColumns,
        "Payment" => PaymentColumns,
        "Expense" => ExpenseColumns,
        "Loan" => LoanColumns,
        _ => throw new ArgumentException($"The import type '{entityType}' is not supported.", nameof(entityType)),
    };

    /// <summary>Normalizes a client-supplied entity type to its canonical name, or throws.</summary>
    private static string Canonicalize(string entityType)
    {
        var normalized = NormalizeEnumToken(entityType);
        var synonym = normalized switch
        {
            "tenants" => "Tenant",
            "properties" => "Property",
            "units" => "Unit",
            "payments" or "rent" or "rents" or "transactions" => "Payment",
            "expenses" or "expense" or "mortgagepayment" or "mortgagepayments" => "Expense",
            "loans" or "mortgage" or "mortgages" => "Loan",
            _ => null,
        };

        if (synonym != null)
        {
            return synonym;
        }

        foreach (var supported in ICsvImportService.SupportedEntityTypes)
        {
            if (string.Equals(entityType, supported, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        throw new ArgumentException(
            $"The import type '{entityType}' is not supported. Choose tenants, properties, units, payments, expenses, or loans.",
            nameof(entityType));
    }
}

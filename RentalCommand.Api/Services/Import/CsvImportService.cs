using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Import;

/// <inheritdoc cref="ICsvImportService"/>
public sealed class CsvImportService : ICsvImportService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IUnitCsvImportPreviewQuery _unitPreview;
    private readonly ICoreCsvImportPreviewQuery _corePreview;

    // Defined column sets per entity type (the order doubles as the downloadable template header).
    private static readonly string[] TenantColumns = ["firstName", "lastName", "email", "phone"];
    private static readonly string[] PropertyColumns = ["name", "addressLine1", "addressLine2", "city", "state", "postalCode", "type"];
    private static readonly string[] UnitColumns = ["propertyName", "propertyId", "unitNumber", "bedrooms", "bathrooms", "marketRent"];
    private static readonly string[] PaymentColumns = ["relationshipNumber", "propertyName", "unitNumber", "paymentType", "amount", "paidDate", "method", "externalReference", "notes"];
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly string[] ExpenseColumns = ["propertyName", "category", "description", "amount", "incurredAt", "paidAt", "notes"];
    private static readonly string[] LoanColumns = ["propertyName", "lender", "originalAmount", "currentBalance", "annualInterestRatePct", "termMonths", "startDate", "dayOfMonthDue", "monthlyPrincipalInterest", "monthlyEscrow"];

    public CsvImportService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IUnitCsvImportPreviewQuery unitPreview,
        ICoreCsvImportPreviewQuery corePreview)
    {
        _db = db;
        _atomic = atomic;
        _unitPreview = unitPreview;
        _corePreview = corePreview;
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
        var portfolioId = scope.PortfolioId;
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

        var batch = await BuildBatchContextAsync(portfolioId, canonicalType, rows, columnIndex, ct);

        var rowResults = new List<CsvImportRowResult>(table.Rows.Count);
        var validCount = 0;
        var createdCount = 0;
        var duplicateCount = 0;

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = row.Cells;

            string? Cell(string column) =>
                columnIndex.TryGetValue(column, out var idx) && idx < cells.Length
                    ? cells[idx].Trim()
                    : null;

            var errors = new List<string>();
            batch.BeginRow(row.RowNumber);

            // Build + validate the create request, then (when committing) create it. A failure of any
            // single row is captured as that row's errors and never aborts the whole import.
            long? createdId = null;
            var valid = false;
            try
            {
                switch (canonicalType)
                {
                    case "Payment":
                        valid = await TryImportPaymentAsync(portfolioId, Cell, batch, dryRun,
                            commandContext, row.RowNumber, errors, id => createdId = id, ct);
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

            string? skipReason = null;
            var isDuplicate = errors.Count == 0 && batch.TryGetSkip(row.RowNumber, out skipReason);
            if (isDuplicate)
            {
                duplicateCount++;
            }

            rowResults.Add(new CsvImportRowResult
            {
                RowNumber = row.RowNumber,
                Valid = valid,
                Errors = errors,
                CreatedId = createdId,
                IsDuplicate = isDuplicate,
                SkipReason = skipReason,
            });
        }

        return new CsvImportResult
        {
            EntityType = canonicalType,
            DryRun = dryRun,
            TotalRows = rows.Count,
            ValidRows = validCount,
            CreatedRows = createdCount,
            DuplicateRows = duplicateCount,
            Rows = rowResults,
        };
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
                    errors.Add($"type '{typeRaw}' is not a valid property type. Allowed: {string.Join(", ", Enum.GetNames<PropertyType>())}.");
            }
            TryValidate(request, errors);
            return new AtomicPropertyImportRow(
                row.RowNumber, request.Name, request.AddressLine1, request.AddressLine2,
                request.City, request.State, request.PostalCode, (int)request.PropertyType,
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

    private async Task<bool> TryImportPaymentAsync(
        int portfolioId,
        Func<string, string?> cell,
        ImportBatchContext batch,
        bool dryRun,
        CsvImportCommandContext? commandContext,
        int rowNumber,
        List<string> errors,
        Action<long> setId,
        CancellationToken ct)
    {
        var tenantAccountId = ResolveTenantAccountReference(
            NullIfEmpty(cell("relationshipNumber")),
            NullIfEmpty(cell("propertyName")),
            NullIfEmpty(cell("unitNumber")),
            batch,
            errors);

        var amount = ParseRequiredDecimal("amount", cell("amount"), errors);
        var paidDate = ParseRequiredDate("paidDate", cell("paidDate"), errors);
        var paymentType = ParseEnum("paymentType", cell("paymentType"), PaymentType.Rent, errors);

        if (errors.Count > 0 || !tenantAccountId.HasValue || !amount.HasValue || !paidDate.HasValue || !paymentType.HasValue)
        {
            return false;
        }

        var externalReference = NullIfEmpty(cell("externalReference"));
        var dedupeKey = PaymentKey(tenantAccountId.Value, amount.Value, paidDate.Value, externalReference);
        if (batch.ExistingPaymentKeys.Contains(dedupeKey))
        {
            batch.SkipCurrent("duplicate of existing payment");
            return true;
        }
        if (!batch.SeenPaymentKeys.Add(dedupeKey))
        {
            batch.SkipCurrent("duplicate payment row in this file");
            return true;
        }

        if (!dryRun)
        {
            var context = commandContext!.Value;
            var method = NullIfEmpty(cell("method")) ?? "Imported payment";
            var notes = NullIfEmpty(cell("notes"));
            var description = string.IsNullOrWhiteSpace(notes)
                ? $"Imported {paymentType.Value} payment"
                : notes;
            var naturalDigest = HashKey(dedupeKey);
            var command = new RecordTenantReceiptCommand(
                portfolioId,
                tenantAccountId.Value,
                amount.Value,
                DateOnly.FromDateTime(paidDate.Value),
                description,
                method,
                externalReference,
                null,
                null,
                null,
                null,
                true,
                context.ActorUserId,
                context.AuthSessionId,
                context.AccessContextId,
                context.AccessRevision,
                CapabilityKeys.MoneyPaymentsManage,
                $"csv-receipt:{naturalDigest}",
                $"csv-receipt:{portfolioId}:{context.OperationKeyDigest}:{rowNumber}");
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
                command,
                ReceiptCodec,
                ct);
            setId(outcome.Value.LedgerEntryId);
        }

        return true;
    }

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

    private sealed record ReferenceResolution(int? Id, string? Error)
    {
        public static ReferenceResolution Found(int id) => new(id, null);
        public static ReferenceResolution Failed(string error) => new(null, error);
    }

    private sealed class ImportBatchContext
    {
        private int _currentRowNumber;
        private readonly Dictionary<int, string> _skipReasons = [];

        public Dictionary<string, ReferenceResolution> PropertiesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReferenceResolution> AccountsByRelationshipNumber { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReferenceResolution> CurrentAccountsByProperty { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReferenceResolution> CurrentAccountsByPropertyUnit { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingPaymentKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenPaymentKeys { get; } = new(StringComparer.Ordinal);

        public void BeginRow(int rowNumber) => _currentRowNumber = rowNumber;

        public void SkipCurrent(string reason) => _skipReasons[_currentRowNumber] = reason;

        public bool TryGetSkip(int rowNumber, out string? reason) =>
            _skipReasons.TryGetValue(rowNumber, out reason);
    }

    private async Task<ImportBatchContext> BuildBatchContextAsync(
        int portfolioId,
        string canonicalType,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        CancellationToken ct)
    {
        var batch = new ImportBatchContext();

        string? Cell(ImportRow row, string column) =>
            columnIndex.TryGetValue(column, out var idx) && idx < row.Cells.Length
                ? NullIfEmpty(row.Cells[idx].Trim())
                : null;

        if (canonicalType is "Unit")
        {
            var propertyIds = rows
                .Select(row => Cell(row, "propertyId"))
                .Where(raw => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(raw => int.Parse(raw!, NumberStyles.Integer, CultureInfo.InvariantCulture))
                .Distinct()
                .ToList();

            if (propertyIds.Count > 0)
            {
                var inScopeIds = await _db.Properties
                    .AsNoTracking()
                    .Where(p => p.PortfolioId == portfolioId && propertyIds.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                foreach (var id in propertyIds)
                {
                    batch.PropertyIdsInScope[id] = inScopeIds.Contains(id);
                }
            }
        }

        if (canonicalType is "Unit" or "Expense" or "Loan" or "Payment")
        {
            var propertyNames = rows
                .Select(row => Cell(row, "propertyName"))
                .Where(name => name != null)
                .Select(NormalizeKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (propertyNames.Count > 0)
            {
                var propertyMatches = await _db.Properties
                    .AsNoTracking()
                    .Where(p => p.PortfolioId == portfolioId && propertyNames.Contains(p.Name.ToLower()))
                    .GroupBy(p => p.Name.ToLower())
                    .Select(g => new { Name = g.Key, Count = g.Count(), Id = g.Min(p => p.Id) })
                    .ToListAsync(ct);

                foreach (var name in propertyNames)
                {
                    var match = propertyMatches.SingleOrDefault(p => p.Name == name);
                    batch.PropertiesByName[name] = match switch
                    {
                        null => ReferenceResolution.Failed($"No property named '{{0}}' was found in this portfolio."),
                        { Count: > 1 } => ReferenceResolution.Failed($"Property name '{{0}}' is ambiguous — more than one property matches. Use propertyId instead."),
                        _ => ReferenceResolution.Found(match.Id),
                    };
                }
            }
        }

        if (canonicalType is "Payment")
        {
            await LoadTenantAccountReferencesAsync(portfolioId, rows, columnIndex, batch, ct);
            await LoadExistingPaymentKeysAsync(portfolioId, rows, columnIndex, batch, ct);
        }
        return batch;
    }

    private async Task LoadTenantAccountReferencesAsync(
        int portfolioId,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        ImportBatchContext batch,
        CancellationToken ct)
    {
        string? Cell(ImportRow row, string column) =>
            columnIndex.TryGetValue(column, out var idx) && idx < row.Cells.Length
                ? NullIfEmpty(row.Cells[idx].Trim())
                : null;

        var relationshipNumbers = rows
            .Select(row => Cell(row, "relationshipNumber"))
            .Where(value => value != null)
            .Select(NormalizeKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (relationshipNumbers.Count > 0)
        {
            var relationshipMatches = await _db.TenantAccounts
                .AsNoTracking()
                .Where(account => account.PortfolioId == portfolioId
                    && relationshipNumbers.Contains(account.LeaseManagement!.RelationshipNumber.ToLower()))
                .GroupBy(account => account.LeaseManagement!.RelationshipNumber.ToLower())
                .Select(group => new
                {
                    RelationshipNumber = group.Key,
                    Count = group.Count(),
                    Id = group.Min(account => account.Id),
                })
                .ToListAsync(ct);

            foreach (var relationshipNumber in relationshipNumbers)
            {
                var match = relationshipMatches.SingleOrDefault(
                    row => row.RelationshipNumber == relationshipNumber);
                batch.AccountsByRelationshipNumber[relationshipNumber] = match switch
                {
                    null => ReferenceResolution.Failed($"No rental relationship numbered '{{0}}' was found in this portfolio."),
                    { Count: > 1 } => ReferenceResolution.Failed($"Rental relationship number '{{0}}' is ambiguous."),
                    _ => ReferenceResolution.Found(match.Id),
                };
            }
        }

        var propertyNames = rows
            .Where(row => Cell(row, "relationshipNumber") == null && Cell(row, "propertyName") != null)
            .Select(row => Cell(row, "propertyName")!)
            .Select(NormalizeKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (propertyNames.Count == 0)
        {
            return;
        }

        var accountsByProperty = await (
                from occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { occupancy.PortfolioId, Id = occupancy.CurrentLeaseManagementId }
                    equals new { management.PortfolioId, Id = (int?)management.Id }
                join account in _db.TenantAccounts.AsNoTracking()
                    on new { management.PortfolioId, LeaseManagementId = management.Id }
                    equals new { account.PortfolioId, account.LeaseManagementId }
                where occupancy.PortfolioId == portfolioId
                    && propertyNames.Contains(management.Property!.Name.ToLower())
                group account by management.Property!.Name.ToLower()
                into grouped
                select new { PropertyName = grouped.Key, Count = grouped.Count(), Id = grouped.Min(row => row.Id) })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var propertyName in propertyNames)
        {
            var match = accountsByProperty.SingleOrDefault(row => row.PropertyName == propertyName);
            batch.CurrentAccountsByProperty[propertyName] = match switch
            {
                null => ReferenceResolution.Failed($"No current tenant account was found for property '{{0}}'."),
                { Count: > 1 } => ReferenceResolution.Failed($"Property '{{0}}' has more than one current tenant account. Add unitNumber or relationshipNumber."),
                _ => ReferenceResolution.Found(match.Id),
            };
        }

        var unitNumbers = rows
            .Where(row => Cell(row, "relationshipNumber") == null && Cell(row, "propertyName") != null && Cell(row, "unitNumber") != null)
            .Select(row => Cell(row, "unitNumber")!)
            .Select(NormalizeKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unitNumbers.Count == 0)
        {
            return;
        }

        var accountsByPropertyUnit = await (
                from occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { occupancy.PortfolioId, Id = occupancy.CurrentLeaseManagementId }
                    equals new { management.PortfolioId, Id = (int?)management.Id }
                join account in _db.TenantAccounts.AsNoTracking()
                    on new { management.PortfolioId, LeaseManagementId = management.Id }
                    equals new { account.PortfolioId, account.LeaseManagementId }
                where occupancy.PortfolioId == portfolioId
                    && propertyNames.Contains(management.Property!.Name.ToLower())
                    && unitNumbers.Contains(management.Unit!.UnitNumber.ToLower())
                group account by new
                {
                    PropertyName = management.Property!.Name.ToLower(),
                    UnitNumber = management.Unit!.UnitNumber.ToLower(),
                }
                into grouped
                select new
                {
                    grouped.Key.PropertyName,
                    grouped.Key.UnitNumber,
                    Count = grouped.Count(),
                    Id = grouped.Min(row => row.Id),
                })
            .ToListAsync(ct);

        foreach (var propertyName in propertyNames)
        {
            foreach (var unitNumber in unitNumbers)
            {
                var key = LeasePropertyUnitKey(propertyName, unitNumber);
                var match = accountsByPropertyUnit.SingleOrDefault(
                    row => row.PropertyName == propertyName && row.UnitNumber == unitNumber);
                batch.CurrentAccountsByPropertyUnit[key] = match switch
                {
                    null => ReferenceResolution.Failed($"No current tenant account was found for property '{{0}}' and unit '{{1}}'."),
                    { Count: > 1 } => ReferenceResolution.Failed($"Property '{{0}}' and unit '{{1}}' have more than one current tenant account. Use relationshipNumber."),
                    _ => ReferenceResolution.Found(match.Id),
                };
            }
        }
    }

    private async Task LoadExistingPaymentKeysAsync(
        int portfolioId,
        IReadOnlyList<ImportRow> rows,
        IReadOnlyDictionary<string, int> columnIndex,
        ImportBatchContext batch,
        CancellationToken ct)
    {
        var candidates = rows
            .Select(row => new
            {
                Amount = TryParseDecimalForLookup(Cell(row, "amount", columnIndex)),
                EffectiveDate = TryParseDateForLookup(Cell(row, "paidDate", columnIndex)),
            })
            .Where(x => x.Amount.HasValue && x.EffectiveDate.HasValue)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var amounts = candidates.Select(c => c.Amount!.Value).Distinct().ToList();
        var minDate = candidates.Min(c => c.EffectiveDate!.Value);
        var maxDate = candidates.Max(c => c.EffectiveDate!.Value);

        var minEffectiveOn = DateOnly.FromDateTime(minDate);
        var maxEffectiveOn = DateOnly.FromDateTime(maxDate);
        var existing = await _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                && amounts.Contains(entry.Amount)
                && entry.EffectiveOn >= minEffectiveOn
                && entry.EffectiveOn <= maxEffectiveOn)
            .Select(entry => new
            {
                entry.TenantAccountId,
                entry.Amount,
                entry.EffectiveOn,
                ExternalReference = entry.ProviderPaymentAttempt == null
                    ? null
                    : entry.ProviderPaymentAttempt.ProviderObjectId,
            })
            .ToListAsync(ct);

        foreach (var payment in existing)
            batch.ExistingPaymentKeys.Add(PaymentKey(
                payment.TenantAccountId,
                payment.Amount,
                payment.EffectiveOn.ToDateTime(TimeOnly.MinValue),
                payment.ExternalReference));
    }

    private static string? Cell(ImportRow row, string column, IReadOnlyDictionary<string, int> columnIndex) =>
        columnIndex.TryGetValue(column, out var idx) && idx < row.Cells.Length
            ? NullIfEmpty(row.Cells[idx].Trim())
            : null;

    private static int? ResolveTenantAccountReference(
        string? relationshipNumber,
        string? propertyName,
        string? unitNumber,
        ImportBatchContext batch,
        List<string> errors)
    {
        if (relationshipNumber != null)
        {
            if (!batch.AccountsByRelationshipNumber.TryGetValue(
                    NormalizeKey(relationshipNumber), out var resolution))
            {
                errors.Add($"No rental relationship numbered '{relationshipNumber}' was found in this portfolio.");
                return null;
            }

            if (resolution.Error != null)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, resolution.Error, relationshipNumber));
                return null;
            }

            return resolution.Id;
        }

        if (propertyName == null)
        {
            errors.Add("A relationshipNumber or propertyName is required.");
            return null;
        }

        var normalizedProperty = NormalizeKey(propertyName);
        ReferenceResolution? leaseResolution;
        if (unitNumber != null)
        {
            var normalizedUnit = NormalizeKey(unitNumber);
            var key = LeasePropertyUnitKey(normalizedProperty, normalizedUnit);
            if (!batch.CurrentAccountsByPropertyUnit.TryGetValue(key, out leaseResolution))
            {
                errors.Add($"No current tenant account was found for property '{propertyName}' and unit '{unitNumber}'.");
                return null;
            }

            if (leaseResolution.Error != null)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, leaseResolution.Error, propertyName, unitNumber));
                return null;
            }

            return leaseResolution.Id;
        }

        if (!batch.CurrentAccountsByProperty.TryGetValue(normalizedProperty, out leaseResolution))
        {
            errors.Add($"No current tenant account was found for property '{propertyName}'.");
            return null;
        }

        if (leaseResolution.Error != null)
        {
            errors.Add(string.Format(CultureInfo.InvariantCulture, leaseResolution.Error, propertyName));
            return null;
        }

        return leaseResolution.Id;
    }

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

        errors.Add($"{column} '{raw}' is not valid. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }

    private static decimal? TryParseDecimalForLookup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim().TrimStart('$').Replace(",", string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static DateTime? TryParseDateForLookup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value)
            ? value
            : null;
    }

    private static string PaymentKey(int tenantAccountId, decimal amount, DateTime effectiveDate, string? externalReference) =>
        string.Join("|", tenantAccountId, amount.ToString("0.00", CultureInfo.InvariantCulture), DateKey(effectiveDate), NormalizeKey(externalReference));

    private static string HashKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string DateKey(DateTime value) =>
        value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string LeasePropertyUnitKey(string propertyName, string unitNumber) =>
        $"{propertyName}|{unitNumber}";

    private static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

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
        _ => throw new ArgumentException($"Unsupported import entity type '{entityType}'.", nameof(entityType)),
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
            $"Unsupported import entity type '{entityType}'. Supported: {string.Join(", ", ICsvImportService.SupportedEntityTypes)}.",
            nameof(entityType));
    }
}

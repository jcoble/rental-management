using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Import;

public readonly record struct CsvImportCommandContext(
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long AccessRevision,
    string OperationKeyDigest);

/// <summary>
/// Bulk CSV import for a migrating landlord: upload a spreadsheet of core records and transactions
/// and create them in bulk, with a dry-run preview (per-row validation + errors) before committing.
/// Reuses the existing per-entity create services + their DataAnnotations validation — no field
/// validation is duplicated here. Always portfolio-scoped (the id is supplied by the controller from
/// the JWT, never the client).
/// </summary>
public interface ICsvImportService
{
    /// <summary>The entity types this service can import (case-insensitive at the API edge).</summary>
    static readonly IReadOnlyList<string> SupportedEntityTypes = ["Tenant", "Property", "Unit", "Payment", "Expense", "Loan"];

    /// <summary>
    /// Parses <paramref name="csv"/>, maps each row to the relevant create request, validates it
    /// (DataAnnotations + the service's own portfolio/reference checks), and — when
    /// <paramref name="dryRun"/> is false — creates the valid rows via the existing domain service.
    /// Invalid rows are skipped with their errors collected; a single bad row never aborts the run.
    /// </summary>
    /// <param name="entityType">Supported entity type (case-insensitive).</param>
    /// <exception cref="CsvFormatException">The CSV is structurally unusable (no header row).</exception>
    /// <exception cref="ArgumentException">The entity type is not supported.</exception>
    Task<CsvImportResult> ImportAsync(
        WorkspaceReadScope scope,
        string entityType,
        Stream csv,
        bool dryRun,
        CsvImportCommandContext? commandContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the CSV header row (column names, comma-joined) for an entity type, so the web can
    /// offer a downloadable, ready-to-fill template.
    /// </summary>
    /// <exception cref="ArgumentException">The entity type is not supported.</exception>
    string GetTemplate(string entityType);
}

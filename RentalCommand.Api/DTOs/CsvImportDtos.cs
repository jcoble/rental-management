namespace RentalCommand.Api.DTOs;

/// <summary>
/// Result of a CSV bulk import. When <c>DryRun</c> is true the rows are
/// validated but nothing is created (<c>CreatedRows</c> = 0 and every <c>CreatedId</c> is null);
/// when false the valid rows are created via the existing domain services and invalid rows are
/// skipped (their errors are still reported). Frank-friendly: a single bad row never aborts the run.
/// </summary>
public sealed class CsvImportResult
{
    public string EntityType { get; init; } = string.Empty;
    public bool DryRun { get; init; }

    /// <summary>Total data rows found after the header (excludes blank lines).</summary>
    public int TotalRows { get; init; }

    /// <summary>Rows that passed validation (mappable + valid create request).</summary>
    public int ValidRows { get; init; }

    /// <summary>Rows actually persisted. Always 0 on a dry run.</summary>
    public int CreatedRows { get; init; }

    /// <summary>Rows that validated but were skipped because the natural key already exists.</summary>
    public int DuplicateRows { get; init; }

    public IReadOnlyList<CsvImportRowResult> Rows { get; init; } = [];
}

/// <summary>Per-row outcome: 1-based row number (matching the spreadsheet, header = row 1),
/// whether it validated, any human-readable errors, and the created id on a committed import.</summary>
public sealed class CsvImportRowResult
{
    /// <summary>1-based row number as the user sees it in their spreadsheet (the header is row 1,
    /// so the first data row is row 2).</summary>
    public int RowNumber { get; init; }

    public bool Valid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Id of the created entity when committed; null on dry run or for skipped/invalid rows.</summary>
    public int? CreatedId { get; init; }

    /// <summary>True when this row matched an existing record and was intentionally skipped.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Human-readable reason for a valid skipped row, such as a duplicate natural key.</summary>
    public string? SkipReason { get; init; }
}

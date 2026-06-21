namespace RentalCommand.Api.DTOs;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Year-end view DTOs (spec §11 + §18). The "what last year's tool never showed him" deliverable: per
// property + portfolio, the two key numbers side by side — CASH FLOW (what hit the pocket) vs TAXABLE
// INCOME (Schedule E) — with depreciation and debt service finally present, plus the rent roll. Money
// is decimal; every figure is computed DB-side by the corrected report services.

/// <summary>The year-end three-block view for a tax year: cash flow, tax/Schedule-E, and rent roll.</summary>
public class YearEndViewResponse
{
    public int Year { get; set; }

    /// <summary>Block 1 — true cash flow (rent − opex − debt service) per property + portfolio.</summary>
    public CashFlowSummaryResponse CashFlow { get; set; } = new();

    /// <summary>Block 2 — taxable income / Schedule E (interest + depreciation in, principal out).</summary>
    public ScheduleEReport ScheduleE { get; set; } = new();

    /// <summary>Block 3 — rent roll: one row per current lease.</summary>
    public IReadOnlyList<YearEndRentRollRow> RentRoll { get; set; } = [];

    /// <summary>
    /// "See your accountant" caveats (spec §18) the owner must never trust the raw numbers past — the
    /// figures above do NOT model these, so the UI surfaces them as labels.
    /// </summary>
    public IReadOnlyList<string> AccountantNotes { get; set; } = [];
}

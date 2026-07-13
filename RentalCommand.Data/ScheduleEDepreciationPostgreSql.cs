using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>
/// Database-owned Schedule E depreciation calculation. Keeping this as a mapped SQL function lets
/// every report compose property/asset eligibility, authorization, UNION, grouping, and totals into
/// the same translated statement without duplicating the tax formula in report LINQ.
/// </summary>
public static class ScheduleEDepreciationDbFunction
{
    public const string Name = "rc_schedule_e_depreciation_amount";

    public static decimal AnnualAmount(
        decimal? costBasis,
        decimal? landValue,
        DateTime? inServiceDate,
        decimal? manualAnnualDepreciation,
        int method,
        decimal recoveryYears,
        int convention,
        decimal accumulatedDepreciation,
        int taxYear)
        => throw new InvalidOperationException(
            $"{Name} may only be evaluated by the database provider.");

    internal static void Configure(ModelBuilder modelBuilder)
    {
        var method = typeof(ScheduleEDepreciationDbFunction).GetMethod(
            nameof(AnnualAmount),
            [
                typeof(decimal?), typeof(decimal?), typeof(DateTime?), typeof(decimal?),
                typeof(int), typeof(decimal), typeof(int), typeof(decimal), typeof(int),
            ]) ?? throw new InvalidOperationException($"Could not resolve {nameof(AnnualAmount)}.");

        modelBuilder.HasDbFunction(method).HasName(Name);
    }
}

internal static class ScheduleEDepreciationFunctionSql
{
    public const string Drop =
        "DROP FUNCTION IF EXISTS rc_schedule_e_depreciation_amount(numeric, numeric, timestamp with time zone, numeric, integer, numeric, integer, numeric, integer);";

    public const string Create = """
        CREATE OR REPLACE FUNCTION rc_schedule_e_depreciation_amount(
          cost_basis numeric,
          land_value numeric,
          in_service_date timestamp with time zone,
          manual_annual_depreciation numeric,
          depreciation_method integer,
          recovery_years numeric,
          depreciation_convention integer,
          accumulated_depreciation numeric,
          tax_year integer)
        RETURNS numeric
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        AS $function$
          WITH basis_row AS (
            SELECT CASE
                     WHEN cost_basis IS NULL THEN NULL::numeric
                     ELSE round(cost_basis - COALESCE(land_value, 0), 2)
                   END AS basis,
                   COALESCE(accumulated_depreciation, 0) AS accumulated,
                   EXTRACT(YEAR FROM in_service_date AT TIME ZONE 'UTC')::integer AS in_service_year,
                   EXTRACT(MONTH FROM in_service_date AT TIME ZONE 'UTC')::integer AS in_service_month
          ), calculation_row AS (
            SELECT basis,
                   GREATEST(COALESCE(basis - accumulated, 0), 0) AS remaining,
                   in_service_year,
                   in_service_month,
                   tax_year - in_service_year AS year_index,
                   CASE
                     WHEN manual_annual_depreciation IS NOT NULL
                       THEN round(manual_annual_depreciation, 2)
                     WHEN basis IS NULL OR basis <= 0 OR in_service_year IS NULL
                          OR tax_year < in_service_year OR recovery_years <= 0
                       THEN 0
                     WHEN depreciation_method = 0 AND depreciation_convention = 0
                       THEN round(
                         basis / recovery_years
                         * CASE
                             WHEN tax_year = in_service_year
                               THEN 12 - in_service_month + 0.5
                             ELSE 12
                           END / 12,
                         2)
                     WHEN depreciation_method = 0 AND depreciation_convention = 1
                          AND tax_year - in_service_year <= recovery_years
                       THEN round(
                         basis / recovery_years
                         * CASE
                             WHEN tax_year - in_service_year = 0
                                  OR tax_year - in_service_year = recovery_years
                               THEN 0.5
                             ELSE 1
                           END,
                         2)
                     WHEN depreciation_method = 1 AND depreciation_convention = 1
                       THEN round(
                         basis * CASE
                           WHEN recovery_years = 5 THEN CASE tax_year - in_service_year
                             WHEN 0 THEN 0.20 WHEN 1 THEN 0.32 WHEN 2 THEN 0.192
                             WHEN 3 THEN 0.1152 WHEN 4 THEN 0.1152 WHEN 5 THEN 0.0576
                             ELSE 0 END
                           WHEN recovery_years = 7 THEN CASE tax_year - in_service_year
                             WHEN 0 THEN 0.1429 WHEN 1 THEN 0.2449 WHEN 2 THEN 0.1749
                             WHEN 3 THEN 0.1249 WHEN 4 THEN 0.0893 WHEN 5 THEN 0.0892
                             WHEN 6 THEN 0.0893 WHEN 7 THEN 0.0446 ELSE 0 END
                           WHEN recovery_years = 15 THEN CASE tax_year - in_service_year
                             WHEN 0 THEN 0.05 WHEN 1 THEN 0.095 WHEN 2 THEN 0.0855
                             WHEN 3 THEN 0.077 WHEN 4 THEN 0.0693 WHEN 5 THEN 0.0623
                             WHEN 6 THEN 0.059 WHEN 7 THEN 0.059 WHEN 8 THEN 0.0591
                             WHEN 9 THEN 0.059 WHEN 10 THEN 0.0591 WHEN 11 THEN 0.059
                             WHEN 12 THEN 0.0591 WHEN 13 THEN 0.059 WHEN 14 THEN 0.0591
                             WHEN 15 THEN 0.0295 ELSE 0 END
                           ELSE 0
                         END,
                         2)
                     ELSE 0
                   END AS requested_amount
            FROM basis_row
          )
          SELECT round(
                   GREATEST(
                     CASE
                       WHEN basis IS NULL THEN requested_amount
                       ELSE LEAST(requested_amount, remaining)
                     END,
                     0),
                   2)
          FROM calculation_row;
        $function$;
        """;
}

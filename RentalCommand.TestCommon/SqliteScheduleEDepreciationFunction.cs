using Microsoft.Data.Sqlite;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

/// <summary>
/// Registers the SQLite test-double for the canonical PostgreSQL depreciation function. Production
/// never branches by provider: report LINQ always calls the mapped database function. SQLite service
/// tests install the same signature and delegate its arithmetic to the pinned domain calculator;
/// PostgreSQL integration tests remain authoritative for the deployed SQL body and composition.
/// </summary>
public static class SqliteScheduleEDepreciationFunction
{
    public static void RegisterScheduleEDepreciationFunctionForSqlite(this SqliteConnection connection)
    {
        connection.CreateFunction<
            decimal?, decimal?, DateTime?, decimal?, int, decimal, int, decimal, int, decimal>(
            ScheduleEDepreciationDbFunction.Name,
            static (
                costBasis,
                landValue,
                inServiceDate,
                manualAnnualDepreciation,
                method,
                recoveryYears,
                convention,
                accumulatedDepreciation,
                taxYear) =>
            {
                if (manualAnnualDepreciation.HasValue || landValue.HasValue)
                {
                    return DepreciationCalculator.AnnualForYear(
                        new PropertyDepreciationBasis(
                            costBasis,
                            landValue,
                            inServiceDate,
                            manualAnnualDepreciation,
                            accumulatedDepreciation),
                        taxYear).Amount;
                }

                if (!costBasis.HasValue || !inServiceDate.HasValue)
                    return 0m;

                return DepreciationCalculator.AnnualForYear(
                    costBasis.Value,
                    inServiceDate.Value,
                    (DepreciationMethod)method,
                    recoveryYears,
                    (DepreciationConvention)convention,
                    accumulatedDepreciation,
                    taxYear).Amount;
            },
            isDeterministic: true);
    }
}

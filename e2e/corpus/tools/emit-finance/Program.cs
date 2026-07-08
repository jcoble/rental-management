using System.Globalization;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Services;

// Authoritative finance emitter for the E2E corpus. Runs the REAL AmortizationCalculator +
// DepreciationCalculator over the frozen scenario so the ground-truth loan/depreciation numbers
// exactly match what DebtServiceService / ScheduleEService will produce in the app.
// Usage: dotnet run -- <scenario.json> <outDir>

var ci = CultureInfo.InvariantCulture;
string scenarioPath = args.Length > 0 ? args[0] : "scenario.json";
string outDir = args.Length > 1 ? args[1] : ".";
Directory.CreateDirectory(outDir);

static string M(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

using var doc = JsonDocument.Parse(File.ReadAllText(scenarioPath));
var root = doc.RootElement;
var corpusEnd = new DateTime(2025, 12, 31);           // emit loan periods due through 2025-12

var loans = new StringBuilder("loan_code,property_code,lender,loan_number,original_amount,rate_pct,term_months,start_date,day_due,mpi,escrow,escrow_covers_taxes,escrow_covers_insurance,balance_2025_01_open,balance_2025_12_close,interest_2025,principal_2025,escrow_2025,debt_service_2025\n");
var pays = new StringBuilder("loan_code,property_code,period_index,period_key,due_date,opening_balance,interest,principal,escrow,total,balance_after,paid_off\n");
var dep = new StringBuilder("property_code,purchase_price,land_value,building_basis,in_service_date,dep_2023,dep_2024,dep_2025,accumulated_through_2024,accumulated_through_2025,is_first_year_2025\n");

foreach (var p in root.GetProperty("properties").EnumerateArray())
{
    string code = p.GetProperty("code").GetString()!;

    // ── Depreciation: iterate from in-service year to accumulate a realistic seed, then 2025. ──
    decimal purchase = p.GetProperty("purchasePrice").GetDecimal();
    decimal land = p.GetProperty("landValue").GetDecimal();
    var inService = DateTime.Parse(p.GetProperty("inServiceDate").GetString()!, ci);
    decimal building = Math.Round(purchase - land, 2, MidpointRounding.AwayFromZero);

    decimal acc = 0m;
    var depByYear = new Dictionary<int, decimal>();
    for (int y = inService.Year; y <= 2024; y++)
    {
        var basisY = new PropertyDepreciationBasis(purchase, land, inService, null, acc);
        decimal d = DepreciationCalculator.AnnualForYear(basisY, y).Amount;
        depByYear[y] = d;
        acc += d;
    }
    decimal accThrough2024 = acc;
    var basis2025 = new PropertyDepreciationBasis(purchase, land, inService, null, accThrough2024);
    var r2025 = DepreciationCalculator.AnnualForYear(basis2025, 2025);
    decimal accThrough2025 = accThrough2024 + r2025.Amount;

    dep.Append(string.Join(',', new[]{
        code, M(purchase), M(land), M(building), inService.ToString("yyyy-MM-dd"),
        M(depByYear.GetValueOrDefault(2023)), M(depByYear.GetValueOrDefault(2024)), M(r2025.Amount),
        M(accThrough2024), M(accThrough2025), r2025.IsFirstYearEstimate ? "true" : "false"
    })).Append('\n');

    // ── Loan amortization (skip paid-off properties). ──
    if (!p.TryGetProperty("loan", out var loan) || loan.ValueKind != JsonValueKind.Object) continue;

    decimal orig = loan.GetProperty("originalAmount").GetDecimal();
    decimal ratePct = loan.GetProperty("ratePct").GetDecimal();
    int term = loan.GetProperty("termMonths").GetInt32();
    var start = DateTime.Parse(loan.GetProperty("startDate").GetString()!, ci);
    int dayDue = loan.GetProperty("dayDue").GetInt32();
    decimal escrow = loan.GetProperty("escrow").GetDecimal();
    bool ct = loan.GetProperty("escrowCoversTaxes").GetBoolean();
    bool cins = loan.GetProperty("escrowCoversInsurance").GetBoolean();

    // Standard fully-amortizing monthly P&I (what a mortgage statement would show), rounded to cents.
    double rMonthly = (double)ratePct / 100.0 / 12.0;
    double mpiRaw = rMonthly == 0.0 ? (double)orig / term
                                    : (double)orig * rMonthly / (1.0 - Math.Pow(1.0 + rMonthly, -term));
    decimal mpi = Math.Round((decimal)mpiRaw, 2, MidpointRounding.AwayFromZero);

    var schedule = AmortizationCalculator.BuildSchedule(orig, ratePct, mpi, escrow, term);

    var startMonth = new DateTime(start.Year, start.Month, 1);
    decimal open2025 = 0m, close2025 = 0m, i25 = 0m, pr25 = 0m, es25 = 0m, ds25 = 0m;
    bool first2025 = true;
    foreach (var row in schedule)
    {
        var month = startMonth.AddMonths(row.PeriodIndex - 1);
        int day = Math.Min(dayDue, DateTime.DaysInMonth(month.Year, month.Month));
        var due = new DateTime(month.Year, month.Month, day);
        if (due > corpusEnd) break;                    // only emit rows in the corpus window

        pays.Append(string.Join(',', new[]{
            loan.GetProperty("loanNumber").GetString()!, code, row.PeriodIndex.ToString(ci),
            $"{month:yyyy-MM}", due.ToString("yyyy-MM-dd"),
            M(row.OpeningBalance), M(row.Interest), M(row.Principal), M(row.Escrow), M(row.Total),
            M(row.BalanceAfter), row.PaidOff ? "true" : "false"
        })).Append('\n');

        if (due.Year == 2025)
        {
            if (first2025) { open2025 = row.OpeningBalance; first2025 = false; }
            close2025 = row.BalanceAfter;
            i25 += row.Interest; pr25 += row.Principal; es25 += row.Escrow; ds25 += row.Total;
        }
    }

    loans.Append(string.Join(',', new[]{
        loan.GetProperty("loanNumber").GetString()!, code, loan.GetProperty("lender").GetString()!,
        loan.GetProperty("loanNumber").GetString()!, M(orig), ratePct.ToString("0.###", ci), term.ToString(ci),
        start.ToString("yyyy-MM-dd"), dayDue.ToString(ci), M(mpi), M(escrow),
        ct ? "true" : "false", cins ? "true" : "false",
        M(open2025), M(close2025), M(i25), M(pr25), M(es25), M(ds25)
    })).Append('\n');
}

File.WriteAllText(Path.Combine(outDir, "loans.csv"), loans.ToString());
File.WriteAllText(Path.Combine(outDir, "loan-payments.csv"), pays.ToString());
File.WriteAllText(Path.Combine(outDir, "depreciation.csv"), dep.ToString());
Console.WriteLine($"Wrote loans.csv, loan-payments.csv, depreciation.csv to {Path.GetFullPath(outDir)}");

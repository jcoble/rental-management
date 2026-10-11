# emit-finance — authoritative loan/depreciation ground-truth

Standalone dev tool (NOT in `RentalCommand.sln`). Runs the app's own
`RentalCommand.Core/Services/AmortizationCalculator` + `DepreciationCalculator` over the frozen
scenario, so ground-truth loan chains and depreciation exactly match what `DebtServiceService` /
`ScheduleEService` produce — avoiding a bit-inexact JS re-port of C# `decimal` division rounding.

**Run (one build — coordinate with the serialized build slot; the clock impl holds it):**
```bash
cd /Users/blackcolours/dev/work/rental-management
MSBUILDDISABLENODEREUSE=1 dotnet run --project e2e/corpus/tools/emit-finance -- \
  e2e/corpus/scenario/scenario.json e2e/corpus/ledger
dotnet build-server shutdown
```

Emits into `e2e/corpus/ledger/`: `loans.csv`, `loan-payments.csv` (every period due ≤ 2025-12),
`depreciation.csv` (2023/2024/2025 + accumulated seed). These overwrite the Phase-1 `*.sample.csv`
illustrations with the real chains. `MonthlyPrincipalInterest` is the standard fully-amortizing
payment computed from (principal, rate, term) — the value the `Loan` entity will carry.

Feature-invariant: the 7 domain-gap fixes (owner-distribution record, proration, bulk import, etc.)
do not change base amortization or building depreciation, so this output stays valid across the
corpus revision. (Gap #4 capital-improvement depreciation would *add* separate schedules, not alter
these.)

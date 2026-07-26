# TSK-750 Step 6 — Portfolio rent consistency discovery brief

**Discovery verdict:** ROADMAP DISCOVERY READY  
**Active goal:** TSK-750 mobile UI rescue  
**Roadmap boundary:** Step 6 — Portfolio rent consistency  
**Prior terminal checkpoint:** Step 5 is blocked at deployed identity/UI proof:
the protected API container is absent and `/health` returns 502. Step 6 does
not absorb or waive that blocker.  
**Planning retry:** 0 of 3

## Goal

Keep scheduled current-month ledger obligations and currently governing lease
rent as two distinct server-authoritative values while qualifying the mobile
rent KPI so users do not infer that the two totals should be equal.

## Observable acceptance

1. **RENT-CONSISTENCY-01 — Divergent DB-side fixture:** A focused
   PostgreSQL-backed test seeds unequal current-month ledger rent charges and
   currently governing lease rent, returns both authoritative totals unchanged
   and property-scoped, and records exactly one analytics SQL statement.
2. **RENT-CONSISTENCY-02 — SQL authority preserved:** The intercepted statement
   retains `authorized_properties`, `TenantLedgerEntries`,
   `vw_lease_agreement_status`, DB-side joins, and DB-side `sum`. No Flutter or
   post-materialization reconciliation is introduced.
3. **RENT-CONSISTENCY-03 — DTO definitions:** DTO documentation defines
   `MonthRentScheduled` as current-month ledger `RentCharge` obligations and
   `MonthlyRecurringRent` as currently governing agreement base rent plus
   active recurring-rent addenda.
4. **RENT-CONSISTENCY-04 — Qualified mobile copy:** The Portfolio KPI uses
   `Signed lease rent` with the qualifier `currently governing`, continues to
   read `monthlyRecurringRent` directly, and no longer contains
   `Monthly Rent` with the generic `recurring` qualifier.
5. **RENT-CONSISTENCY-05 — Real mobile proof:** At 1080 × 2400, Money →
   Portfolio continues to show `$15,000.00 of $20,175.00` for Collection Rate
   and `$1,050.00` for signed, currently governing lease rent, with no overflow
   or runtime error.

## Explicit non-goals

- Do not force scheduled and governing rent totals to match.
- No analytics SQL, lifecycle projection, seed/bootstrap, repository, JSON
  field, authorization, RLS, rent-charge generation, legal-artifact generation,
  addendum, or financial-domain change.
- No database reset, broad reseed, production-stack restoration, or protected
  environment mutation.
- No Step 5 repair, blocker waiver, or completion claim.
- No Step 7, later roadmap work, broad test, cleanup, hardening, fallback,
  compatibility lane, or unrelated audit defect.
- Preserve every existing dirty or untracked WIP path.

## Resolved discovery questions

1. **What do the two values mean?** `MonthRentScheduled` is the sum of
   current-month `TenantLedgerEntries` with type `RentCharge`.
   `MonthlyRecurringRent` is governing agreement base rent plus active
   recurring-rent addenda. Evidence: `S6-ANALYTICS-SQL`.
2. **Why do the demo totals differ?** The demo creates current-month rent
   charges for all occupied units, while only the first agreement has the
   finalized legal-document intent needed to be currently governing. The
   divergence is deliberate lifecycle state, not an aggregation defect.
   Evidence: `S6-SEED-LIFECYCLE`, `S6-HISTORY-4AD5BE50`.
3. **Where is the contradiction?** Mobile displays `monthRentScheduled` under
   Collection Rate and `monthlyRecurringRent` under the generic
   `Monthly Rent / recurring` copy. Transport maps both API fields directly.
   Evidence: `S6-AUDIT-25`, `S6-MOBILE-DIRECT`.
4. **What is the smallest repair?** Correct DTO documentation, add a divergent
   one-statement fixture, and qualify mobile copy without changing either
   value's source.

## Exact execution map

- Modify documentation only in
  `RentalCommand.Api/DTOs/AnalyticsDtos.cs`.
- Modify the divergent one-SQL fixture/check only in
  `RentalCommand.Api.Tests/Domain/AnalyticsServiceAuthorizationTests.cs`.
- Modify rent KPI copy only in
  `mobile/lib/features/analytics/insights_screen.dart`.
- Create
  `mobile/test/analytics_rent_labels_test.dart`.
- Real-device proof may create only:
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.png`
  and
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.xml`.

## Exact serial commands

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~AnalyticsServiceAuthorizationTests
dotnet build-server shutdown
```

From `mobile/`, run serially:

```bash
flutter test test/analytics_rent_labels_test.dart
flutter analyze lib/features/analytics/insights_screen.dart test/analytics_rent_labels_test.dart
```

- The API test command maps to RENT-CONSISTENCY-01 and
  RENT-CONSISTENCY-02.
- Build-server shutdown is required serial resource cleanup only.
- The Flutter test and analyze commands map to RENT-CONSISTENCY-04.
- The read-only relevance review maps the complete diff to
  RENT-CONSISTENCY-02 through RENT-CONSISTENCY-04 and the exact file boundary.
- Real 1080 × 2400 Android proof maps to RENT-CONSISTENCY-05; static checks
  cannot replace it.

## Stop condition

Discovery is complete because the two authoritative definitions, deliberate
demo lifecycle divergence, exact documentation/test/copy owners, commands, and
real-device target are resolved. If the protected API remains absent or returns
502, implementation may pass its static/automated checks but Step 6 remains
`UI PROOF BLOCKED`; do not waive the proof or restore the stack within this
contract.

## Evidence references

- `S6-ROADMAP` — approved Step 6 boundary.
- `S6-AUDIT-25` — reproduced generic-label contradiction.
- `S6-MOBILE-DIRECT` — direct transport and KPI presentation ownership.
- `S6-ANALYTICS-SQL` — one-statement ledger/governing SQL definitions.
- `S6-SEED-LIFECYCLE` — deliberate demo lifecycle divergence.
- `S6-HISTORY-4AD5BE50` — finalized demo agreement history.
- `S5-TERMINAL-BLOCKER` — protected API absence and 502 proof blocker.

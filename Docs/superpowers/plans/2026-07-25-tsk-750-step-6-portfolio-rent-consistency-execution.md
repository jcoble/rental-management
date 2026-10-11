# TSK-750 Step 6 — Portfolio rent consistency execution contract

**Goal:** Present scheduled ledger rent and currently governing lease rent as
distinct, clearly qualified server-authoritative values.  
**Source brief:**
`Docs/superpowers/plans/2026-07-25-tsk-750-step-6-portfolio-rent-consistency-discovery.md`  
**Active goal:** TSK-750 mobile UI rescue  
**Active boundary:** Step 6 — Portfolio rent consistency  
**Plan state:** Ready for execution  
**Planning retry:** 0 of 3

## Preserved checkpoint

Step 5 remains terminally blocked at deployed identity and UI proof because the
protected API container is absent and `/health` returns 502. This independent
Step 6 contract does not absorb, repair, waive, or claim completion of Step 5.
Preserve all existing implementation, test, plan, result, and proof WIP.

## Contract acceptance

1. **RENT-CONSISTENCY-01 — Divergent one-SQL proof:** A focused
   PostgreSQL-backed fixture uses unequal current-month ledger rent charges and
   currently governing lease rent, returns both totals unchanged and
   property-scoped, and records exactly one analytics SQL statement.
2. **RENT-CONSISTENCY-02 — SQL definitions unchanged:** The intercepted
   statement retains `authorized_properties`, `TenantLedgerEntries`,
   `vw_lease_agreement_status`, DB-side joins, and DB-side `sum`. No client or
   post-materialization reconciliation is added.
3. **RENT-CONSISTENCY-03 — Accurate DTO documentation:**
   `MonthRentScheduled` is documented as current-month ledger `RentCharge`
   obligations; `MonthlyRecurringRent` is documented as currently governing
   agreement base rent plus active recurring-rent addenda.
4. **RENT-CONSISTENCY-04 — Qualified mobile label:** The rent KPI displays
   `Signed lease rent` with qualifier `currently governing`, still binds
   directly to `monthlyRecurringRent`, and rejects the generic
   `Monthly Rent / recurring` copy.
5. **RENT-CONSISTENCY-05 — Real Android proof:** At 1080 × 2400, Money →
   Portfolio shows `$15,000.00 of $20,175.00` for Collection Rate and
   `$1,050.00` as signed, currently governing lease rent, with no overflow or
   runtime error.

## Explicit non-goals

- Do not force the two totals equal.
- No analytics SQL, lifecycle view, seed/bootstrap, repository, JSON field,
  authorization, RLS, rent-charge, legal-artifact, addendum, or financial
  behavior change.
- No database reset, broad reseed, API deployment/restoration, or protected
  environment mutation.
- No Step 5 change or proof waiver.
- No Step 7, later roadmap work, broad test, cleanup, hardening, fallback,
  compatibility work, or unrelated audit finding.

## Supported flow and proof target

- **UI impact:** Yes — KPI copy changes on the Portfolio analytics screen.
- **Supported flow:** In the existing authenticated demo management experience,
  open Money → Portfolio and read Collection Rate beside the signed/currently
  governing lease-rent KPI.
- **Required target:** Real Android emulator at 1080 × 2400 using the protected
  Rental Command API and existing demo portfolio.
- **Proof artifacts:**
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.png`
  and
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.xml`.
- **Known blocker:** If the Step 5 state persists—no protected API container or
  `/health` returns 502—the UI gate returns `UI PROOF BLOCKED`. It is not
  waived, replaced by static checks, or repaired within this contract.

## Step 1 — Qualify governing rent without reconciling totals

**Status:** Active  
**Acceptance:** RENT-CONSISTENCY-01 through RENT-CONSISTENCY-05

### Allowed files

- Modify documentation only:
  `RentalCommand.Api/DTOs/AnalyticsDtos.cs`.
- Modify the divergent one-SQL fixture/check only:
  `RentalCommand.Api.Tests/Domain/AnalyticsServiceAuthorizationTests.cs`.
- Modify rent KPI copy only:
  `mobile/lib/features/analytics/insights_screen.dart`.
- Create:
  `mobile/test/analytics_rent_labels_test.dart`.
- The real-device verifier may create only:
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.png`
  and
  `Docs/Reviews/artifacts/tsk-750/step6-portfolio-rent-consistency/portfolio-rent-consistency.xml`.

No other file may be created or modified.

### Required actions

1. Update only the relevant DTO documentation so
   `MonthRentScheduled` names current-month ledger `RentCharge` obligations and
   `MonthlyRecurringRent` names currently governing agreement base rent plus
   active recurring-rent addenda. Do not alter DTO fields or serialization.
2. Add one focused divergent fixture/check to
   `AnalyticsServiceAuthorizationTests.cs`: unequal scheduled ledger rent and
   governing recurring rent must remain distinct, property-scoped output from
   exactly one intercepted SQL statement. Assert that the SQL retains
   `authorized_properties`, `TenantLedgerEntries`,
   `vw_lease_agreement_status`, DB-side joins, and DB-side `sum`.
3. Change only the KPI copy in `insights_screen.dart` to
   `Signed lease rent` with qualifier `currently governing`. Preserve the
   direct `monthlyRecurringRent` binding and all other KPI behavior.
4. Create the focused source-contract test requiring the two qualified strings,
   direct `monthlyRecurringRent` use, and absence of the generic
   `Monthly Rent / recurring` copy.
5. Run the focused API and Flutter commands serially. Run a read-only relevance
   review of the complete implementation diff against the four code/test paths
   and RENT-CONSISTENCY-01 through RENT-CONSISTENCY-04; require
   `RELEVANCE PASS`.
6. After relevance passes, drive the real 1080 × 2400 Money → Portfolio flow
   and capture the two named artifacts. Require RENT-CONSISTENCY-05 and
   `UI PROOF PASS`. If the protected API is absent or returns 502, return
   `UI PROOF BLOCKED` and leave Step 6 incomplete.

### Targeted commands

Run serially:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~AnalyticsServiceAuthorizationTests
dotnet build-server shutdown
```

From `mobile/`, run serially:

```bash
flutter test test/analytics_rent_labels_test.dart
flutter analyze lib/features/analytics/insights_screen.dart test/analytics_rent_labels_test.dart
```

- The API test command proves RENT-CONSISTENCY-01 and
  RENT-CONSISTENCY-02.
- `dotnet build-server shutdown` is required serial resource cleanup only; it
  is not acceptance evidence.
- The Flutter commands prove RENT-CONSISTENCY-04.
- The read-only relevance review of the complete diff proves the four-file
  scope and RENT-CONSISTENCY-02 through RENT-CONSISTENCY-04.
- Real-device PNG/XML proof proves RENT-CONSISTENCY-05.

### Relevance gate

The reviewer checks the complete implementation diff and verifies only:

- documentation changes in `AnalyticsDtos.cs`;
- the divergent one-SQL fixture/check in
  `AnalyticsServiceAuthorizationTests.cs`;
- rent KPI copy in `insights_screen.dart`;
- the new `analytics_rent_labels_test.dart`;
- unchanged analytics SQL, lifecycle, seed, repository, JSON fields,
  authorization, and direct `monthlyRecurringRent` binding;
- RENT-CONSISTENCY-01 through RENT-CONSISTENCY-04 and focused command results.

Require `RELEVANCE PASS`. Any reconciliation of the two values, production
query/lifecycle/seed/auth change, or additional changed path is a blocker.

### Real-device proof gate

At 1080 × 2400, drive Money → Portfolio in the existing demo experience and
require:

- Collection Rate still reads `$15,000.00 of $20,175.00`;
- the `$1,050.00` card reads `Signed lease rent` and
  `currently governing`;
- no overflow or runtime error;
- the named PNG and UI XML exist.

Require `UI PROOF PASS`. If the protected API remains unavailable or returns
502, return `UI PROOF BLOCKED`; do not waive the gate, reset/reseed data,
restore/deploy the stack, or infer proof from static checks.

## Completion gate

- [ ] Every change maps to one of the four allowed code/test paths or two proof
      artifacts.
- [ ] The divergent fixture returns unequal authoritative totals from one SQL
      statement.
- [ ] DB-side authorization, joins, aggregation, and direct transport remain.
- [ ] DTO documentation states both actual definitions.
- [ ] Qualified mobile copy and source-contract test pass.
- [ ] Focused API and Flutter commands pass serially.
- [ ] Fresh relevance review returns `RELEVANCE PASS`.
- [ ] Fresh real-device proof returns `UI PROOF PASS`; a blocked API leaves
      Step 6 incomplete.

## Deferred boundaries

- Step 5 deployed identity, endpoint samples, and emulator performance proof
  remain deferred at their recorded terminal blocker.
- Step 7 and every later roadmap boundary remain deferred.

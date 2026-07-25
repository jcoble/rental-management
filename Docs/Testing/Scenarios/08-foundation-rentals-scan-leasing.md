# Scenario 08 — Foundation Rentals, Scan & Leasing

**Domain:** Rentals and leasing. **Tester session:** `foundation-rentals`.

## Mission

Prove that a landlord can move naturally from rental setup through an occupied, governed rental
without encountering an obvious dead end: create one-rental and multi-rental properties, navigate
the Unit Command Center, capture/import records with inherited context, process an application,
prepare move-in, and manage immutable agreement versions through correction, renewal, and
month-to-month conversion.

This is a real UI walkthrough against the replacement foundation, not a route-existence smoke test.
Every successful-looking mutation must be reopened from its canonical list/detail surface and, when
useful, corroborated through the API or database. Do not generate screenshots; record observable UI
state, URLs, request/response behavior, console errors, and persisted identifiers in the result file.

## Foundation invariants under test

- `Property.RentalStructure` changes presentation/setup only. `SingleRental` and `MultiRental` use
  the same Property, Unit, LeaseManagement, Agreement, account, and calculation model. A
  `SingleRental` property still owns exactly one explicit Unit.
- Unit is the physical Command Center. Global lists and Unit actions open the same canonical
  records; a contextual create/scan inherits the Unit without forcing the user to choose it again.
- LeaseManagement is the continuous household/account relationship. Its TenantAccount, balance,
  deposit, parties, and history survive Agreement correction and renewal.
- Issued/executed LeaseAgreement rows and PDFs are immutable. Correct, Restate, Renew, and
  Month-to-month create explicit draft successor versions; the current governing agreement remains
  effective until its successor governs.
- Applications do not become occupants merely by approval. Prepare move-in creates the planned
  relationship/account/agreement draft atomically; possession is a separate lifecycle fact.
- Scan/import creates a reviewable draft first. Confirmation is atomic and idempotent, and the
  stored source is linked to the resulting canonical record.
- All list filtering, searching, sorting, joins, grouping, totals, and paging execute DB-side in one
  translated SQL query/view. No load-then-filter, per-row lookup, or N+1 behavior is acceptable.

## Code map to read before browser work

- Web routes: `web/src/routes/(protected)/properties/`, `units/`, `owners/`, `tenants/`,
  `applications/`, `leases/`, and `scan/`.
- Unit and lease UI: `web/src/routes/(protected)/units/[id]/+page.svelte`,
  `web/src/routes/(protected)/leases/[id]/+page.svelte`, and components under
  `web/src/lib/components/{unit,leases,applications,scan}/`.
- Web clients: `web/src/lib/api/endpoints/properties.ts`, `units.ts`, `owners.ts`, `tenants.ts`,
  `applications.ts`, `lease-managements.ts`, `lease-addendums.ts`, and `import.ts` plus scan query/
  mutation modules under `web/src/lib/queries` and `web/src/lib/mutations`.
- API: `PropertyController`, `UnitController`, `OwnerEntityController`, `TenantController`,
  `ApplicationsController`, `ScanController`, `LeaseManagementController`,
  `LeaseAgreementController`, and `LeaseAddendumController`.
- Query/mutation services: Property, Unit, OwnerEntity, Tenant, Application, UnitDashboard,
  LeaseManagement query/commands, Agreement draft/issuance/signing, and scan preparation/
  confirmation. Inspect generated SQL for every list/summary changed during bug fixing.

## Preconditions

1. Preview points to the exact source SHA recorded in the run summary and is reachable at
   `https://redacted-host.example.invalid`.
2. Sign in through the visible login UI as a management user. Do not bypass login with an API cookie.
3. Use a portfolio where setup can be exercised without destroying another tester's data. Do not
   reset shared sample data while another scenario is active.
4. Use marker `QA-RSL-<HHMMSS>` in every created name/reference so records can be distinguished.
5. Record all created public/database IDs in the result file. Never edit/delete unmarked seed data.

## Walkthrough A — Global rental navigation and list behavior

1. From the signed-in landing page, open Rentals and visit Properties, Owners, Units, Tenants,
   Leases, and Applications using only visible navigation.
2. On every list, verify: initial load, useful empty state, search, relevant filters, sorting if
   offered, next/previous paging, Add action, row click, browser Back, and refreshed persistence.
3. Search for a unique QA marker and a value that cannot match. Confirm result count and empty copy
   agree and no unrelated portfolio records appear.
4. Follow one record into its detail, then follow at least one relationship link back to the
   Property/Unit/Tenant/Agreement it claims to represent.
5. Watch requests for duplicate list calls or per-row follow-ups. Correlate any slow/wrong count with
   the service query and generated SQL.

## Walkthrough B — Property/Unit setup, including one-rental presentation

1. Create an Owner and reopen it from Owners.
2. Create a **One rental** property with that Owner. Verify one atomic setup action creates the
   Property and its one explicit Unit; no second Unit form/save is required and no zeroed/orphan Unit
   is left after a validation error or retry.
3. Open the property detail. It should describe the address as one rental without making the user
   manage a meaningless unit-number hierarchy, while still linking to the canonical Unit.
4. Create a **Multiple rentals** property and two distinct Units. Confirm unit counts, vacancy, and
   occupancy summaries update from DB-side projections.
5. Edit permitted Property and Unit fields, reopen them, and confirm `RentalStructure` cannot be
   casually changed after setup if doing so would violate the one-unit invariant.
6. Exercise blank required fields, duplicate Unit labels within a Property, invalid addresses, and
   a retry/double-submit. A conflict must be explained in the UI and must not leave partial data.

## Walkthrough C — Unit Command Center and contextual actions

1. Enter the Unit from the global Units list and from its Property detail; both must land on the same
   canonical Unit Command Center route.
2. Traverse every top-level Unit section/tab that is available: Overview, Listing, Applications,
   Tenant & lease, Ledger/Money, Turnover, Work/Maintenance, Documents, and Timeline/History.
3. Confirm the global Rentals peer navigation is no longer competing with the Unit section tabs;
   browser Back returns to the originating list/property without losing the canonical Unit route.
4. Verify each section shows only data for this Unit, relationship links resolve to the correct
   records, and concise header facts (vacancy/current relationship/balance/open work/lease end) agree
   with their canonical detail surfaces.
5. Start Add/Scan actions from the Unit and verify the Unit/Property context is visibly inherited.
   Start the equivalent action globally and verify unresolved context is requested exactly once.

## Walkthrough D — Contextual scan/import, review, and confirmation

Exercise at least three types: Lease, Application, and one of Payment/Expense/Work order.

1. Start a Lease scan from the QA Unit. Confirm the capture/review surface shows the inherited
   Property and Unit before upload and does not silently route to another rental.
2. Start an Application scan from the Unit/Application context. Start the third scan globally.
3. Upload representative files. Confirm status/progress remains understandable while extraction is
   pending, fails, retries, or completes. Provider-unavailable must be an explicit recoverable state.
4. On review, compare every extracted name, amount, date, address, party, Unit, and target type to the
   source. Correct at least one extracted value and one resolved relationship before confirming.
5. Confirm once, then deliberately retry/double-submit with the same operation. Exactly one canonical
   record must exist and the replay must return that record, not create a duplicate.
6. Open the created canonical record and retrieve the stored source through its file route. Confirm
   the reviewed values persisted, the source belongs to that record, and the return link restores the
   Unit/global context where capture began.
7. Reject/abandon one draft. No canonical record or orphan relationship may be created.
8. Exercise batch/import only if exposed: malformed file, mixed valid/invalid rows, preview, commit,
   same-file reselect, and navigation to created records.

## Walkthrough E — Applications and prepare move-in

1. Create/import an Application for the vacant QA Unit. Reopen from global Applications and the Unit
   Applications section; both surfaces must show the same status and applicant.
2. Correct landlord-editable submitted details and confirm persistence. Exercise required-field and
   invalid-email/amount/date validation.
3. Move the Application through Under review and Approve. Confirm Decline/Withdraw destructive
   actions explain their result and do not expose illegal transitions.
4. From the approved Application, choose Prepare move-in. Verify Unit and applicant are preselected,
   then complete the stepper with a primary tenant, optional co-tenant/guarantor/occupant, terms,
   signers, dates, deposit, rent, and template.
5. Submit once and retry once. The atomic result must contain one LeaseManagement, one TenantAccount,
   one initial Agreement draft, the intended parties/signers, and no partial rows on validation or
   conflict. The Application must point to the prepared relationship exactly once.
6. Confirm approval/preparation does not mark the Unit occupied until possession is actually given.

## Walkthrough F — Initial Agreement draft, issue, signing, and possession

1. Open the prepared LeaseManagement from both the Unit Tenant & lease section and global Leases.
2. Review/edit the initial Agreement draft through its actual builder/stepper. Confirm parties,
   signers, term type, rent, due day, deposit, late fee, terms, and document template survive reopening.
3. Prepare/issue it. Verify a PDF/artifact is created from the reviewed immutable values, required
   signers and signing order are correct, and issue retry returns the same artifact/request.
4. Complete signing using the supported test path. Verify signature progress, issued and executed
   artifacts, evidence timestamps, and Agreement history all update without mutating the issued row.
5. Give possession only after an executed governing Agreement and open TenantAccount exist. Confirm
   Unit occupancy changes and the same TenantAccount is visible in Unit/Lease/Money views.

## Walkthrough G — Correction/versioning

1. On the executed governing Agreement choose **Correct**, enter a clear reason (for example a
   misspelled legal name or corrected move-in date), and open the prefilled successor draft.
2. Verify the UI explicitly explains that a corrected version will be issued and re-signed; do not
   present this as an in-place edit of the signed contract.
3. Change only the intended field, review the full prefilled draft, issue, and complete signatures.
4. Confirm Agreement history shows both versions and links predecessor/successor. The old PDF/row is
   unchanged, the successor governs from its stated date, and the LeaseManagement/TenantAccount ID,
   balance, deposit, parties, notices, and history did not fork.
5. Cancel an abandoned successor draft and verify the source Agreement continues governing.

## Walkthrough H — Renewal and month-to-month

Use separate marked relationships or cancel unissued drafts so workflows do not conflict.

1. Choose **Renew** on a current fixed-term Agreement. Confirm the successor is prefilled, its term
   starts after the current term, and any effective Addendum series requires an explicit End,
   Incorporate into base, or Reissue decision.
2. Issue/execute a future renewal. Before its governing date, confirm the current Agreement remains
   governing while the renewal appears as Upcoming. Advance the simulation clock only through the
   supported UI and verify the governing projection switches without a background status rewrite.
3. Confirm the relationship/account, deposit, and ledger remain continuous across the renewal.
4. Choose **Month-to-month** on another current fixed-term Agreement. The successor must have
   `TermType=MonthToMonth`, a concrete start/governing date, and no fake end date.
5. Exercise overlapping dates, missing correction reason, start-before/at current end, duplicate
   successor attempt, and an unresolved addendum decision. Each must fail clearly without partial
   Agreement/supersession rows.

## Cross-cutting error and security checks

- Refresh/deep-link every canonical detail route used above; no feature may depend solely on hidden
  tab/modal state.
- Use Back/Close after dialogs/steppers and confirm context is preserved without duplicate submits.
- Record every console error and failed request. A generic spinner that stops with no error is a bug.
- Confirm inaccessible or deleted IDs return a clear 403/404 state, not another portfolio's record.
- Probe one cross-portfolio Property/Unit/Tenant/Application/Lease ID only through authorized test
  fixtures; list pickers and server mutations must both reject it.
- Confirm tenant/account/Agreement lists page server-side and no page issues per-row requests.

## Output

Write the report to:
`Docs/Testing/Results/2026-07-15-001/08-foundation-rentals-scan-leasing.md`

For every section record **Pass**, **Fail**, or **Blocked**, exact actions, URLs, created IDs,
request/status evidence, console errors, and file:line references. Use the `BUG-N` format from
`Docs/Testing/exploratory-tester.md`. Separate proven bugs from unexecuted/blocked flows.

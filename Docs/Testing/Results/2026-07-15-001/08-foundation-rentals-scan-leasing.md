# Exploratory Test Report: Foundation Rentals, Scan & Leasing

Date: 2026-07-15  
Tester: `foundation-rentals`  
Source SHA: `8419a55ff9ae850ef833431ac1c5cff65fd85bd7`
Environment: `https://redacted-host.example.invalid`  
Status: Fail — source fixes pending serialized verification and preview refresh

## Scenario

Walk the real management UI from rental setup through contextual scan/import, application approval,
move-in preparation, and immutable Agreement correction/renewal/month-to-month workflows.

## Summary

The exact refreshed preview SHA was exercised at a `1440 x 900` browser viewport. Management login,
global rental lists, all six Unit Command Center sections, global/contextual Scan/Add, the Leases
list, one Ending relationship, its draft Agreement, and its ending-decision dialog render. The
Units list now returns instead of loading forever, but remained severely slow and flaky (roughly
11–20 seconds). A freshly generated public application link immediately returns 404 because the
anonymous route has no token-scoped RLS coordinate; that blocks application submission, approval,
move-in, and the later executed-Agreement correction/renewal journeys on this preview.

## Section Status

| Section | Status | Evidence / blocker |
|---|---|---|
| A. Global rental navigation and lists | Fail | All lists render, but Units takes roughly 11–20 seconds and can outlive the UI request window (BUG-2) |
| B. One-rental and multi-rental setup | Blocked | Mutation proof deferred until the refreshed preview; source contract still requires one explicit Unit |
| C. Unit Command Center | Pass with data concern | `/units/10` and all six top-level sections render; seeded facts disagree about possession/account/agreement state |
| D. Contextual scan/import | Fail | Global chooser works; Unit launch does not visibly name the inherited Unit/property (source fix added) |
| E. Applications and prepare move-in | Blocked | Freshly generated public link immediately returns 404 under anonymous RLS (BUG-4) |
| F. Agreement draft/issue/sign/possession | Blocked | Ending relationship, draft version, household, tenant account, possession, and decision dialog render; no executed fixture and BUG-4 blocks creating one |
| G. Correction/versioning | Blocked | No executed governing Agreement exists in demo data and BUG-4 blocks producing one through the UI |
| H. Renewal/month-to-month | Blocked | Ending decision dialog renders all choices; successor workflow needs an executed governing Agreement |
| Cross-cutting errors/security/query behavior | Fail | Dashboard and Units expose slow-query failures; global list paging/search otherwise remains URL/DB-backed |

## Bugs Found

### BUG-1 — Dashboard main content fails immediately after valid login

- **Severity:** High; every management user lands on this broken screen.
- **Repro:** Submit valid credentials on `/login`, wait on `/`, then reload and wait another 15
  seconds.
- **Observed:** The authenticated shell renders, but main content becomes `Failed to load dashboard.`
  Requests to `/api/v1/portfolios/1/dashboard`, `/api/v1/accounting/snapshot`, and
  `/api/v1/ai/briefing` repeatedly abort or remain pending. Neighboring conversations, work-orders,
  appointments, auth-context, sandbox-state, and notification requests return 200.
- **Expected:** Dashboard cards render or each failed dependency presents a bounded/retryable state
  without replacing the entire dashboard.
- **Disposition:** Reported to the orchestrator because Dashboard is outside this Rentals slice;
  Rentals remains reachable through the rendered sidebar.

### BUG-2 — Units list remains severely slow and intermittently outlives the UI request window

- **Severity:** High; users cannot discover or open Units from the canonical global list.
- **Repro:** Open `/units` from Rentals and wait more than 12 seconds.
- **Observed:** `/api/v1/units/list-with-health/page?take=20` returned 200 in `11,238–11,788 ms` on
  two measured runs. A fresh navigation was still at `Loading…` after 20 seconds. Rows eventually
  rendered in the successful runs, but the experience remains visibly broken.
- **Expected:** The paged list returns within the UI request window; a genuine timeout must render a
  retryable error, never an endless loading row.
- **Fix in working tree:** Replaced the per-Unit correlated document count with one page-scoped
  PostgreSQL `UNION`/`GROUP BY` aggregate and replaced the work-order scalar with a grouped join.
  The endpoint now remains exactly three queries for a non-empty page: count, authorized/sorted/
  paged health rows, and document counts for only those returned Unit IDs. Verification is pending
  the orchestrator's serialized build/test and preview refresh.

### BUG-4 — A freshly generated public application link is immediately invalid

- **Severity:** Launch blocking; no applicant can enter the application-to-move-in journey.
- **Repro:** Sign in, open `/applications`, click **Get application link**, then open the exact
  returned `/apply/{token}` URL.
- **Observed:** The public page says the link is invalid or expired. Its exact API request returns
  404 `{"error":"This application link is invalid or no longer active."}`.
- **Root cause:** The anonymous request opens PostgreSQL with no workspace access context. RLS hides
  `Portfolios`, so the exact opaque token cannot resolve even though the authenticated rotation
  command just stored and returned it. Anonymous submission has the same boundary.
- **Fix in working tree:** The connection interceptor now places a validated base64url token in a
  dedicated GUC only for `/api/v1/public/applications/{token}`. A database-owned predicate grants
  that exact token SELECT access only to form/occupancy dependencies and INSERT access only to
  `RentalApplications` and `AtomicAuditLogs`; it grants no applicant-row SELECT, update, delete, or
  ordinary workspace access. The duplicate-email precheck was removed and the existing unique
  index violation remains the 409 guard. Focused API/data contract tests were added.

### BUG-3 — Lease list/detail routes retain a previous route's browser title

- **Severity:** Low but obvious navigation/accessibility defect.
- **Repro:** Open `/tenants`, then use Rentals > Leases; separately navigate from Applications to
  `/leases/1`.
- **Observed:** The list retained `Tenants - Rental Command`; the detail retained the Applications
  title (or no meaningful Lease title).
- **Expected:** `Leases - Rental Command` for the list and a relationship-specific Lease title for
  the detail.
- **Fix in working tree:** Added route-level `<svelte:head>` titles in both
  `web/src/routes/(protected)/leases/+page.svelte` and
  `web/src/routes/(protected)/leases/[id]/+page.svelte`.

## Potential Issues

- The public login rendered email/password, Forgot password, Google sign-in, and Create account.
  Submitting the seeded management administrator redirected to `/` and rendered the Dashboard shell.

## Observations

- `/leases/1` presents the canonical continuous LeaseManagement/TenantAccount relationship,
  household, one draft Agreement, and an explicit reconciliation warning. Opening the Agreement
  draft exposes the complete terms and signer editor rather than a legacy Lease row.
- `/units/1` presents six top-level peer sections without the global Rentals tabs competing inside
  the Unit route: Summary, Leasing, Tenant & lease, Money, Maintenance, and Documents & history.
- The Unit summary says `No current lease for this unit` while the LeaseManagement detail shows a
  relationship, draft Agreement, and possession history. This may intentionally mean “no governing
  executed Agreement,” but the wording is ambiguous enough to recheck after canonical reconciliation.
- The global Scan/Add dialog exposes Auto classify, Receipt/Bill, Payment, Maintenance, Lease,
  Application, Mortgage/Loan, and voice-note entry points. Context inheritance and confirmation
  persistence were not yet exercised.
- `/units/10` exposed a contextual Scan/Add dialog, but its copy only said the scan would stay
  connected to the page; it did not name the Unit/property. The working-tree fix supplies and
  renders `Eastland 8-Plex · Unit 1`-style context so the user can verify where confirmation lands.
- `/leases/17` renders Darius Clark's Ending relationship, tenant account #17, current household,
  possession, one draft Agreement, and the **Record decision** dialog with renewal, month-to-month,
  and move-out choices. The list correctly calls out that no agreement currently governs.

## What Was Tested

- `GET /login` through the visible browser route at `1440 x 900`.
- Password login as the seeded management administrator; redirect to `/` succeeded.
- Authenticated Dashboard shell: portfolio selector, grouped management navigation, sample-data
  banner, Scan/Add, Messages, Appointments, Help, theme, and Notifications controls rendered.
- Rentals navigation group opened from the authenticated sidebar.
- `/properties`: 8 sample rows loaded; `Gahanna` search produced the one expected single-rental
  record and URL-backed `?q=Gahanna`; an impossible marker produced the clear filtered empty state.
- `/owners`: list loaded, and row navigation reopened owner `2` with canonical details, documents,
  history, edit, and delete controls.
- `/tenants`: 22 records loaded server-paged; Next moved to URL-backed `?page=2` and displayed
  records 21–22 with correct disabled/enabled pager state.
- `/units`: shell, filters, rows, and Unit navigation rendered, but measured 11–20 second latency
  remains unacceptable (BUG-2).
- `/leases`: data loaded after roughly 13 seconds; rows showed relationships and Agreement draft
  state. Opened LeaseManagement `1` and its Agreement draft editor. List/detail title defects are
  fixed in the working tree (BUG-3).
- `/applications`: empty state and share-link dialog rendered.
- `/units/1`: canonical Unit Command Center loaded directly; summary facts, next action, rent,
  repairs, documents, upcoming items, and six Unit sections rendered.
- Global Scan/Add: all supported classification/capture entry points rendered.
- `/units/10`: Summary, Leasing/Listing/Applications, Tenant & lease, Money, Maintenance, and
  Documents & history all rendered with stable query-backed destinations.
- Contextual Unit Scan/Add: dialog rendered and the missing human-readable context was reproduced.
- `/applications`: generated a new public link; opening the exact returned link reproduced BUG-4.
- `/leases/17`: relationship detail and ending-decision dialog rendered; dialog was canceled without
  changing the seeded relationship.

## Created Test Data

No application, lease, tenant, payment, or work-order records were created. Clicking **Get
application link** rotated the demo portfolio's opaque public application token once; the returned
link was invalid under anonymous RLS and no applicant data was submitted.

# Exploratory Test Report: Foundation Rentals, Scan & Leasing

Date: 2026-07-15  
Tester: `foundation-rentals`  
Source SHA: `2404ec7abc802477569d0dfe7d4fec60b2760636`
Environment: `https://redacted-host.example.invalid`  
Status: In progress

## Scenario

Walk the real management UI from rental setup through contextual scan/import, application approval,
move-in preparation, and immutable Agreement correction/renewal/month-to-month workflows.

## Summary

The public preview is reachable and the seeded management administrator signed in through the
visible `/login` form. The global rental lists, one LeaseManagement detail, its draft Agreement,
one Unit Command Center, and the global Scan/Add chooser were exercised at a `1440 x 900` browser
viewport. The remaining mutation journeys are blocked on a refreshed preview after the focused
Units query and move-in command changes are built and deployed.

## Section Status

| Section | Status | Evidence / blocker |
|---|---|---|
| A. Global rental navigation and lists | Fail | Properties, Owners, Tenants, Leases, and Applications load; Units permanently loads (BUG-2) |
| B. One-rental and multi-rental setup | Blocked | Mutation proof deferred until the refreshed preview; source contract still requires one explicit Unit |
| C. Unit Command Center | In progress | `/units/1` and its six top-level sections render; full section/data agreement remains to be proven |
| D. Contextual scan/import | In progress | Global chooser exposes all expected scan types; upload/review/confirm and inherited Unit context remain to be run |
| E. Applications and prepare move-in | Blocked | Applications empty/share-link state works; no marked Application exists for destructive transition proof |
| F. Agreement draft/issue/sign/possession | In progress | LeaseManagement and draft Agreement editor render; issue/sign/atomic possession mutation not run |
| G. Correction/versioning | Not run | Requires an executed governing Agreement in the refreshed preview |
| H. Renewal/month-to-month | Not run | Requires an executed governing Agreement in the refreshed preview |
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

### BUG-2 — Units list times out into a permanent loading row

- **Severity:** High; users cannot discover or open Units from the canonical global list.
- **Repro:** Open `/units` from Rentals and wait more than 12 seconds.
- **Observed:** The table remains at `Loading…`; the UI repeatedly aborts
  `/api/v1/units/list-with-health/page?take=20`. A direct authenticated request returns the expected
  200 payload, but took `11,757 ms`, beyond the client request window.
- **Expected:** The paged list returns within the UI request window; a genuine timeout must render a
  retryable error, never an endless loading row.
- **Fix in working tree:** Replaced the per-Unit correlated document count with one page-scoped
  PostgreSQL `UNION`/`GROUP BY` aggregate and replaced the work-order scalar with a grouped join.
  The endpoint now remains exactly three queries for a non-empty page: count, authorized/sorted/
  paged health rows, and document counts for only those returned Unit IDs. Verification is pending
  the orchestrator's serialized build/test and preview refresh.

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
- `/units`: shell and filters rendered, but the paged data request timed out (BUG-2).
- `/leases`: data loaded after roughly 13 seconds; rows showed relationships and Agreement draft
  state. Opened LeaseManagement `1` and its Agreement draft editor. List/detail title defects are
  fixed in the working tree (BUG-3).
- `/applications`: empty state and share-link dialog rendered.
- `/units/1`: canonical Unit Command Center loaded directly; summary facts, next action, rent,
  repairs, documents, upcoming items, and six Unit sections rendered.
- Global Scan/Add: all supported classification/capture entry points rendered.

## Created Test Data

None.

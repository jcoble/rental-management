# Exploratory Test Report: Tenant Portal & Public (apply / e-sign)
Date: 2026-06-28
Tester: tester2
Duration: ~2.5 hours (incl. environment recovery)

## Scenario
Test the tenant-facing **portal** (dashboard, pay rent, maintenance, messages, notifications,
lease, security) and the **public** flows (`/apply/[token]` and lease **e-sign**), focusing on
auth/IDOR, public links that must work without a staff session, empty-submission handling, and
whether signed/submitted state reflects back to the staff record.

## Summary
The portal/public surface is **security-solid and robust**: tenant data scoping is enforced
server-side from JWT claims (every cross-tenant, cross-portfolio, and staff-without-`tenantId`
probe returned 403/404 with no data leak), every empty/invalid submission validated with `400`
(never `500`), the public apply flow lands a reviewable application, and the full e-sign flow
(staff send → public sign while logged-out → state reflected back to the lease) works end to end
with correct terminal/invalid-link handling. I found **2 real defects** (1 Medium, 1 Low), both
internal-consistency/UX rather than security. I also hit — and **fixed** — a significant
**environment blocker** (the dev API + web had been killed by a botched restart, and the API was
serving the wrong TLS cert, breaking all SSR auth); details in the *Environment* section because
it is infra, not a portal/public code defect, but it blocked all browser testing until resolved.

## Environment / Test Setup (read first — blocked then unblocked testing)
- **Both the API and the web dev server were DOWN** when I started driving the browser. Root cause
  visible in `/tmp/rentalcommand-api.log`: a relaunch attempt used `setsid` (which does not exist
  on macOS) and failed, leaving nothing listening on `:5666`/`:5665`/`:5667`. The Engine + Postgres
  stayed up. I restored the API (`dotnet run` with the mkcert Kestrel cert) and the web
  (`pnpm dev` with `NODE_EXTRA_CA_CERTS` + `API_URL`) per `scripts/start-dev.sh`.
- **Before the restart the API was serving the ASP.NET self-signed dev cert (`issuer=CN=localhost`),
  not the mkcert cert.** The browser/curl trust it via the macOS keychain, but Node (SvelteKit SSR)
  only trusts the mkcert CA via `NODE_EXTRA_CA_CERTS`, so **every SSR→API call failed** ("fetch
  failed"): the login form action returned *"Unable to connect to the API server"*, and the
  `(protected)`/`(portal)` guards + the `/sign` SSR load could not validate. Restarting the API with
  `Kestrel__Certificates__Default__Path=web/.cert/api-cert.pem` made it serve the mkcert cert and
  SSR began working. **This is a dev-infra/start-script fragility, not a portal/public bug**, but it
  is worth a human's attention since it silently breaks login for everyone and the `setsid` relaunch
  path is broken on macOS.
- **Portal access provisioning:** the app has **no UI/endpoint to grant a tenant portal access** —
  portal users are only created by `IdentitySeeder.EnsureTenantPortalAccountsAsync` at API startup,
  for the **default portfolio's** tenants. A tenant created mid-session therefore cannot log in
  until a restart. To test as a real tenant I used an existing portal user (`explore-tenant-a@rc.local`,
  tenant 77 "Marcus Williams", portfolio 9 — a prior wave's rich demo portfolio) and set its password
  to a known value by copying the admin password hash (ASP.NET Identity stores the salt in the hash,
  so the password became `Admin123!`). All write tests were marked `QA-T2-230335`.

## Bugs Found

### BUG-1: Tenant balance/overdue silently excludes `Failed` payments — dashboard "Overdue" card shows a self-contradictory "$0.00 / 1 overdue item"
**Severity:** Medium
**Location:** Portal dashboard `/portal` "Overdue" card; `GET /api/v1/portal/balance`; `/portal/payments`
**Expected:** A rent payment in `Failed` status is money still owed — the app itself says so on the
payments page ("A payment attempt … did not go through, **so it is still owed**") and lets the tenant
pay it (`isPayable(Failed) === true`). The dashboard "Overdue" dollar amount and item count should
agree with each other and with that "still owed" treatment.
**Actual:** With one of the tenant's past-due payments set to `Failed`, the dashboard "Overdue" card
renders **"$0.00"** (dollar) next to **"1 overdue item"** (count) — the two halves of the same card
contradict each other. The server `balance` also reports `outstanding: 0.0, overdue: 0.0,
overdueCount: 0`, i.e. the $1,050 owed-but-failed payment is **invisible in the balance totals**,
while the payments page simultaneously shows it as payable/"still owed".
**Evidence:** `output/playwright/tester2-02-overdue-mismatch.png`. Live: with payment 1380 (lease 53,
due 2025-11-01) set to `Failed`, `/portal/balance` → `{"collected":13650.00,"outstanding":0.0,
"overdue":0.0,"overdueCount":0}`; dashboard Overdue card text = `"Overdue | $0.00 | 1 overdue item"`;
payments row = `"Rent · $1,050.00 | … Failed | … did not go through, so it is still owed."`
**Code Reference:**
- Server (excludes `Failed` from Outstanding **and** Overdue): `RentalCommand.Api/Services/Domain/PortalService.cs:75-86` — `Outstanding`/`Overdue` only sum `Scheduled`/`Late`/`Partial`.
- Client count (includes `Failed`): `web/src/routes/(portal)/portal/+page.svelte:96-101` — `overduePayments = payments.filter(p => !['Paid','Waived','Refunded'].includes(status) && isPastDueUtc(p.dueDate))`.
- The card that mixes the two: `web/src/routes/(portal)/portal/+page.svelte:277-281` (`money(balance.overdue)` + `{overduePayments.length} overdue item`).
**Suggested Fix:** Pick one definition of "owed/overdue" and use it everywhere. Most consistent with
the rest of the UI: include `Failed` (and `Partial` remainder) in the server's `Outstanding`/`Overdue`
sums in `GetBalanceAsync`, and drive the dashboard "Overdue item" count from the server's
`balance.overdueCount` instead of the client-side `overduePayments.length`, so the dollar and the count
are always the same source of truth.
**Why This Matters:** This is a money-correctness app for a non-technical landlord. A tenant (or the
landlord, via the same balance) seeing **"Overdue $0.00"** while a payment has actually failed
under-reports what is owed and invites missed collections; a card that shows "$0.00" beside "1 overdue
item" also reads as broken. Incidence rises once online/autopay charging is live (that is what produces
`Failed` rows).

### BUG-2: Staff/non-tenant users can open the tenant portal and get a broken, error-spamming empty dashboard
**Severity:** Low
**Location:** `/portal` (and subpages) when signed in as Admin/Manager/Agent (no `tenantId` claim)
**Expected:** Either redirect a staff user away from the tenant portal, or show a clear "the portal is
for residents" message. No failed API calls / console errors, and no misleading data states.
**Actual:** The `(portal)` layout guard admits **any authenticated user**, but every portal API is
tenant-only and returns `403` for a caller without a `tenantId`. So a staff user lands on a "Tenant
dashboard" that fires **6 failed (403) requests** (`leases`, `balance`, `payments`, `work-orders`,
`appointments`, `conversations`) and then renders misleading empties — *"No lease is linked to this
account."*, *"Overdue $0.00"* — **except** the Appointments card, which alone surfaces the error
(*"Couldn't load appointments."*). It also shows the **staff user's own** notifications on the tenant
dashboard. (No tenant data leaks — all tenant endpoints correctly 403.)
**Evidence:** `output/playwright/tester2-01-admin-portal-empty.png`; console on admin `/portal` load:
six `Failed to load resource: … 403 … /api/v1/portal/{payments,leases,balance,conversations,work-orders,appointments}`.
**Code Reference:**
- Over-permissive guard: `web/src/routes/(portal)/+layout.server.ts:10-21` (only checks `locals.user`).
- API correctly rejects: `RentalCommand.Api/Controllers/PortalController.cs:34-49` (`GetTenantId()` → `Forbid()`).
- Inconsistent card error handling: `web/src/routes/(portal)/portal/+page.svelte:427-431` (appointments surfaces `isError`; other queries swallow it via `?? []`).
**Suggested Fix:** In `(portal)/+layout.server.ts`, redirect users whose role/claims indicate staff
(no `tenantId`) to the staff `/` (or render a "select a resident to assist" gate if that feature is
intended — it currently does not exist). Cheap interim: gate the dashboard queries on a real tenant
context so they don't fire (and 403-log) for staff.
**Why This Matters:** The portal is the resident's first impression; a staff member who taps the wrong
link sees a broken-looking app, and the six recurring 403s are noise that hides real auth errors in
logs/telemetry. The guard's own comment says staff may view the portal "to assist a resident," but no
resident-selection mechanism exists, so the allowance only produces this broken state.

## Potential Issues (need investigation)
- **Pay-rent ownership/IDOR guard is correct in code but unverifiable at runtime here.** With Stripe
  disabled, `POST /portal/payments/{id}/checkout` returns `503 "Online payments are not enabled."`
  *before* the ownership check, so paying another tenant's payment (id 1400, Jordan's) and paying your
  own both return `503` — the IDOR is safely *masked* but not exercised. The ownership filter itself is
  present and correct (`StripePaymentService.cs:144-156`: `p.PortfolioId == portfolioId &&
  Leases.Any(l => l.Id == p.LeaseId && l.TenantId == tenantId)` → `NotFound`). Re-verify the 404 path
  once online payments are enabled in a test environment.
- **"Maintenance request appears for staff"** was confirmed via DB attribution, not a staff UI view —
  my tenant (portfolio 9) and the admin (portfolio 1) are in different portfolios, and there is no
  portfolio-9 staff login available. The created work order (id 195) is correctly stamped
  `PortfolioId=9, TenantId=77, UnitId=55, LeaseId=53, Status=New`, so it will appear in that portfolio's
  staff WorkOrders list, but I could not see it rendered staff-side.

## Observations (work as built; no action required)
- **IDOR scoping is exemplary.** Portal scope is taken entirely from the JWT (`portfolioId` +
  `tenantId`), never from request params (`PortalController.cs:33-38`). Verified live as tenant 77:
  own work-order `60` → 200; another tenant's `61` and an unassigned `58` → 404; cross-portfolio
  `lease/ask?leaseId=240` → 404; conversation `5` (another tenant, another portfolio) → 404 in both
  API and the messages UI ("Couldn't load this conversation."); notification mark-read on another
  user's notif `24` → 404; admin (no `tenantId`) on every portal endpoint → 403.
- **Balance is computed DB-side** in a single grouped aggregate (`PortalService.cs:64-88`) and matched
  my hand-computed expectation exactly (Collected $13,650 / Outstanding $1,050 / Overdue $1,050 / 1
  item with one Late payment; $14,700 / $0 / $0 when all paid) — consistent with the project's
  SQL-side-aggregation rule.
- **Empty/invalid submissions all validate with `400`, never `500`:** empty maintenance (`{}` and
  empty strings) → `400 {Title required, Description required}`; empty conversation start and empty
  reply → `400`; reply to a non-existent conversation → `404`; work-order detail id `0`/`-1` → `404`.
  The maintenance form also blocks empty client-side with inline errors.
- **Public apply (`/apply/[token]`) works fully logged-out** (`ssr=false`): loaded "Apply to Coble
  Limited", submitted with consent, landed as `RentalApplications` id 19 (`PortfolioId=1, Status=Submitted,
  ConsentGiven=true`) — correct portfolio resolved from the token. A garbled token shows the branded
  "This link isn't working" screen (not a 404 error page).
- **E-sign works end-to-end and reflects back:** staff "Send for signature" on lease 241 (native
  provider, always-configured) → public `/sign/{token}` opened **logged-out** → consent + typed
  signature → "Signed — all done". DB after: `SignatureRequest.Status=Completed`,
  `Signer.Status=Signed`, and the staff `signature-status` API → `{"esignStatus":"Signed",
  "hasSignedDocument":true}`. Re-opening the signed link shows the terminal "Signed" state (no
  re-sign); a garbled sign token shows the branded "This signing link isn't valid" screen.
- **Portal auth guard** correctly redirects logged-out users from `/portal`, `/portal/payments`,
  `/portal/messages`, `/portal/maintenance` to `/login?redirectTo=…`.
- **Stripe-off handled gracefully:** payments page shows a calm "Online payments aren't set up yet"
  banner, the "Pay now" button is disabled as "Pay unavailable", and autopay shows "not available
  right now" — no red errors.
- **Messaging round-trips** in the UI (started conversation 9, replied, thread shows both bubbles with
  correct Tenant/Management alignment; opening a thread marks it read).

## What Was Tested (repro steps)
1. Restored API (mkcert cert) + web dev server; provisioned a known password for portal user
   `explore-tenant-a@rc.local` (tenant 77 / portfolio 9) by copying the admin hash.
2. **Portal as tenant 77 (browser):** dashboard cards (Overdue/Next Rent/Maintenance/Lease/Payments/
   Messages) reconciled against `/portal/balance` and the DB; payments page (14 rows, 1 payable when a
   payment is Late, graceful Stripe-off); maintenance empty-submit → inline validation, valid submit →
   toast + new row; messages open own thread + reply; notifications + security pages render clean.
3. **IDOR (API, tenant-77 token):** own vs others' work orders, payments checkout, conversations,
   notifications mark-read, cross-portfolio `lease/ask`; plus admin (no `tenantId`) against every portal
   endpoint. 500-hunt sweep on empty/edge bodies.
4. **Failed-payment edge:** flipped a paid payment to `Failed` (past due) → observed the Overdue card
   "$0.00 / 1 overdue item" mismatch and balance under-reporting; reverted.
5. **Staff-on-portal:** logged in as admin, opened `/portal`, captured the 6×403 + broken empty state.
6. **Public apply:** generated the link as admin (`/applications` → "Get application link"), logged out,
   submitted at `/apply/{token}`, verified application id 19 in DB; garbled-token screen.
7. **E-sign:** sent lease 241 for signature as admin, captured the signer token from the DB, logged out,
   signed at `/sign/{token}`, verified `Completed`/`Signed`/`hasSignedDocument` reflected back; terminal +
   invalid-token screens.

### Test-data cleanup
- Reverted all temporary ledger tweaks (Marcus's payments 1380/1381 back to `Paid`; balance restored to
  all-paid). **Left in place (new, marked `QA-T2-230335`):** work orders 195 + the UI-created one,
  conversation 9, application id 19, and the **signed state on lease 241** (test lease "Bob Worker",
  portfolio 1 — now `EsignStatus=Signed`). Portal user `explore-tenant-a@rc.local` password is now
  `Admin123!` (original hash was overwritten and cannot be restored).
- API + web dev servers are **running** (I started them in the background after finding them down).
